using System.Security.Cryptography;

namespace Drive;

public sealed class Uploads(Database database, Storage storage, Settings settings)
{
    private static readonly SemaphoreSlim PartSlots = new(8);

    public async Task<object> Start(Guid user, UploadRequest request)
    {
        var name = Input.Name(request.Name);
        var hash = Input.Hash(request.Sha256);
        if (request.Size < 0 || request.Size > Settings.MaxFileSize)
        {
            throw ApiError.Bad("Files may be at most 5 GiB.");
        }

        var contentType = request.ContentType ?? "application/octet-stream";
        if (contentType.Length > 180 || contentType.Any(char.IsControl))
        {
            throw ApiError.Bad("Invalid content type.");
        }

        await using var db = await database.Open();
        await db.Begin();
        Guid owner = user;
        if (request.EntryId is { } entryId)
        {
            var entry = await Access.Entry(db, entryId, user, edit: true);
            if (entry.Text("kind") != "file")
            {
                throw ApiError.Bad("Upload a new version of a file, not a folder.");
            }

            owner = entry.Id("owner_id");
        }
        await Access.LockOwner(db, owner);
        if (request.EntryId is { } target)
        {
            await Access.Entry(db, target, user, edit: true);
        }
        else
        {
            await Access.Parent(db, request.ParentId, owner);
        }

        var account = await db.One("""
            SELECT quota,
            (SELECT COALESCE(sum(size),0)::bigint FROM versions v JOIN entries e ON e.id=v.entry_id WHERE e.owner_id=$1) AS used,
            (SELECT COALESCE(sum(size),0)::bigint FROM uploads WHERE owner_id=$1 AND state IN ('active','assembling')) AS reserved
            FROM users WHERE id=$1
            """, owner);
        if (request.Size > account!.Number("quota") - account.Number("used") - account.Number("reserved"))
        {
            throw new ApiError(409, "Storage quota exceeded. Remove old versions or permanently delete files.");
        }

        if ((await db.Query("SELECT id FROM uploads WHERE user_id=$1 AND state IN ('active','assembling')", user)).Count >= 5)
        {
            throw new ApiError(409, "Finish or cancel an existing upload first (maximum 5). ");
        }

        var id = Guid.NewGuid();
        var key = $"objects/{owner:N}/{id:N}";
        var multipart = request.Size == 0 ? "" : await storage.Begin(key, contentType);
        try
        {
            await db.Execute("""
                INSERT INTO uploads(id,user_id,owner_id,entry_id,parent_id,name,size,sha256,content_type,object_key,multipart_id)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
                """, id, user, owner, request.EntryId, request.EntryId is null ? request.ParentId : null,
                name, request.Size, hash, contentType, key, multipart);
            await db.Commit();
        }
        catch
        {
            try
            {
                await storage.Delete(key, multipart);
            }
            catch { /* Expiry cleanup cannot see an uncommitted session. */ }
            throw;
        }
        return new
        {
            id,
            partSize = Settings.PartSize
        };
    }

    private static async Task<Row> Session(Session db, Guid user, Guid id, string lockMode = "")
    {
        return await db.One("SELECT * FROM uploads WHERE id=$1 AND user_id=$2 " + lockMode, id, user)
            ?? throw ApiError.Missing();
    }

    private static void Active(Row upload)
    {
        if (upload.Text("state") != "active" || (DateTime)upload["expires_at"]! < DateTime.UtcNow)
        {
            throw new ApiError(409, "This upload is no longer active. Start a new upload.");
        }
    }

    public async Task<object> List(Guid user)
    {
        await using var db = await database.Open();
        return await db.Query("""
            SELECT id,name,size,sha256,state,entry_id,parent_id,created_at,expires_at,result_entry_id
            FROM uploads WHERE user_id=$1 AND state IN ('active','assembling','failed')
            ORDER BY created_at DESC LIMIT 20
            """, user);
    }

    public async Task<object> Status(Guid user, Guid id)
    {
        await using var db = await database.Open();
        var upload = await Session(db, user, id);
        upload.Remove("object_key");
        upload.Remove("multipart_id");
        upload["parts"] = await db.Query("SELECT number,size,sha256 FROM upload_parts WHERE upload_id=$1 ORDER BY number", id);
        upload["part_size"] = Settings.PartSize;
        return upload;
    }

    public async Task<object> Part(Guid user, Guid id, int number, HttpRequest request, CancellationToken ct)
    {
        if (!await PartSlots.WaitAsync(0, ct))
        {
            throw new ApiError(429, "All upload workers are busy. Retry shortly.");
        }

        try
        {
            await using var db = await database.Open(ct);
            await db.Begin();
            await db.Execute("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", $"{id}:{number}");
            var upload = await Session(db, user, id, "FOR SHARE");
            Active(upload);
            var count = (upload.Number("size") + Settings.PartSize - 1) / Settings.PartSize;
            if (number < 1 || number > count)
            {
                throw ApiError.Bad("Invalid part number.");
            }

            var expectedSize = Math.Min(Settings.PartSize, upload.Number("size") - (long)(number - 1) * Settings.PartSize);
            if (request.ContentLength != expectedSize)
            {
                throw ApiError.Bad("Part Content-Length does not match its expected size.");
            }

            var expectedHash = Input.Hash(request.Headers["X-Part-SHA256"].ToString());
            var existing = await db.One("SELECT * FROM upload_parts WHERE upload_id=$1 AND number=$2", id, number);
            if (existing is not null)
            {
                if (existing.Text("sha256") != expectedHash)
                {
                    throw new ApiError(409, "This part was already uploaded with a different checksum.");
                }

                await db.Commit();
                return new
                {
                    number,
                    reused = true
                };
            }
            Directory.CreateDirectory(settings.TempDirectory);
            var path = Path.Combine(settings.TempDirectory, "drive-part-" + Guid.NewGuid().ToString("N"));
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long received = 0;
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > expectedSize)
                {
                    throw ApiError.Bad("Part exceeds its expected size.");
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (received != expectedSize || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash)
            {
                throw ApiError.Bad("Part checksum or size does not match. Retry with the original file.");
            }

            file.Position = 0;
            var etag = await storage.Part(upload, number, file, expectedSize, ct);
            await db.Execute("INSERT INTO upload_parts(upload_id,number,etag,size,sha256) VALUES($1,$2,$3,$4,$5)", id, number, etag, expectedSize, expectedHash);
            await db.Commit();
            return new
            {
                number,
                reused = false
            };
        }
        finally { PartSlots.Release(); }
    }

    public async Task<object> Complete(Guid user, Guid id)
    {
        await using var db = await database.Open();
        await db.Begin();
        var initial = await Session(db, user, id);
        // All metadata mutations lock the owner before an upload, avoiding cross-request deadlocks.
        await Access.LockOwner(db, initial.Id("owner_id"));
        var upload = await Session(db, user, id, "FOR UPDATE");
        if (upload.Text("state") == "complete")
        {
            if (upload.OptionalId("result_entry_id") is not { } completedId)
            {
                throw new ApiError(410, "The uploaded file has been permanently deleted.");
            }

            return new
            {
                id = completedId
            };
        }
        Active(upload);
        var owner = upload.Id("owner_id");
        if (upload.OptionalId("entry_id") is { } target)
        {
            await Access.Entry(db, target, user, edit: true);
        }
        else
        {
            await Access.Parent(db, upload.OptionalId("parent_id"), owner);
        }

        var parts = await db.Query("SELECT * FROM upload_parts WHERE upload_id=$1 ORDER BY number", id);
        var expected = (upload.Number("size") + Settings.PartSize - 1) / Settings.PartSize;
        if (parts.Count != expected || parts.Sum(p => p.Number("size")) != upload.Number("size"))
        {
            throw new ApiError(409, "Some parts are missing. Resume the upload before finishing.");
        }

        await db.Execute("UPDATE uploads SET state='assembling' WHERE id=$1", id);
        await storage.Complete(upload, parts);
        var fingerprint = await storage.Fingerprint(upload.Text("object_key"));
        if (fingerprint.Size != upload.Number("size") || fingerprint.Hash != upload.Text("sha256"))
        {
            await QueueDeletion(db, upload);
            await db.Execute("UPDATE uploads SET state='failed' WHERE id=$1", id);
            await db.Commit();
            throw new ApiError(422, "Whole-file checksum mismatch. The file was not published.");
        }
        var entryId = upload.OptionalId("entry_id") ?? Guid.NewGuid();
        if (upload.OptionalId("entry_id") is null)
        {
            await db.Execute("INSERT INTO entries(id,owner_id,parent_id,name,kind) VALUES($1,$2,$3,$4,'file')", entryId, owner, upload.OptionalId("parent_id"), upload.Text("name"));
        }

        await db.Execute("""
            INSERT INTO versions(id,entry_id,object_key,size,sha256,content_type,created_by)
            VALUES($1,$2,$3,$4,$5,$6,$7)
            """, Guid.NewGuid(), entryId, upload.Text("object_key"), upload.Number("size"), upload.Text("sha256"), upload.Text("content_type"), user);
        await db.Execute("UPDATE entries SET updated_at=now() WHERE id=$1", entryId);
        await db.Execute("UPDATE uploads SET state='complete',result_entry_id=$2 WHERE id=$1", id, entryId);
        await db.Commit();
        return new
        {
            id = entryId
        };
    }

    internal static Task QueueDeletion(Session db, Row upload) => db.Execute("""
        INSERT INTO object_deletions(object_key,multipart_id) VALUES($1,$2) ON CONFLICT DO NOTHING
        """, upload.Text("object_key"), upload.Text("multipart_id"));

    public async Task Cancel(Guid user, Guid id)
    {
        await using var db = await database.Open();
        await db.Begin();
        var initial = await Session(db, user, id);
        await Access.LockOwner(db, initial.Id("owner_id"));
        var upload = await Session(db, user, id, "FOR UPDATE");
        if (upload.Text("state") == "complete")
        {
            throw new ApiError(409, "The upload has completed. Delete the file instead.");
        }

        await QueueDeletion(db, upload);
        await db.Execute("UPDATE uploads SET state='cancelled' WHERE id=$1", id);
        await db.Commit();
    }
}

public record UploadRequest(string Name, long Size, string Sha256, string? ContentType, Guid? ParentId, Guid? EntryId);
