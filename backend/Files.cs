using System.Security.Cryptography;
using System.Text;

namespace Drive;

public static class Access
{
    public static async Task<Row> Entry(Session db, Guid id, Guid user, bool edit = false, bool owner = false, bool includeTrash = false)
    {
        var table = includeTrash ? "entries" : "live_entries";
        var row = await db.One($"""
            SELECT e.*, CASE WHEN e.owner_id=$2 THEN 'owner' ELSE g.role END AS role
            FROM {table} e LEFT JOIN grants g ON g.entry_id=e.id AND g.user_id=$2
            WHERE e.id=$1 AND (e.owner_id=$2 OR g.user_id=$2)
            """, id, user) ?? throw ApiError.Missing();
        if (owner && row.Text("role") != "owner" || edit && row.Text("role") == "viewer")
        {
            throw new ApiError(403, "You do not have permission to change this file.");
        }

        return row;
    }

    public static async Task Parent(Session db, Guid? parent, Guid owner)
    {
        if (parent is null)
        {
            return;
        }

        var row = await Entry(db, parent.Value, owner, owner: true);
        if (row.Text("kind") != "folder")
        {
            throw ApiError.Bad("The destination must be a folder.");
        }

        var depth = await db.One("""
            WITH RECURSIVE ancestors AS (
                SELECT id,parent_id FROM entries WHERE id=$1
                UNION ALL SELECT e.id,e.parent_id FROM entries e JOIN ancestors a ON e.id=a.parent_id
            ) SELECT count(*) AS count FROM ancestors
            """, parent);
        if (depth!.Number("count") >= 64)
        {
            throw ApiError.Bad("Folders may be nested at most 64 levels.");
        }
    }

    public static Task LockOwner(Session db, Guid owner) => db.Execute("SELECT id FROM users WHERE id=$1 FOR UPDATE", owner);
    public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class Files(Database database, Storage storage)
{
    private const string Columns = """
        SELECT e.*, u.email AS owner_email,
            CASE WHEN e.owner_id=$1 THEN 'owner' ELSE g.role END AS role,
            v.id AS version_id, COALESCE(v.size,0) AS size, v.sha256, v.content_type
        FROM live_entries e JOIN users u ON u.id=e.owner_id
        LEFT JOIN grants g ON g.entry_id=e.id AND g.user_id=$1
        LEFT JOIN LATERAL (SELECT * FROM versions WHERE entry_id=e.id ORDER BY selected_at DESC,id DESC LIMIT 1) v ON true
        """;

    public async Task<object> List(Guid user, Guid? parent, string? search, string? view, int offset)
    {
        await using var db = await database.Open();
        if (offset is < 0 or > 100000)
        {
            throw ApiError.Bad("Invalid page offset.");
        }

        var term = (search ?? "").Trim().ToLowerInvariant();
        if (term.Length > 180)
        {
            throw ApiError.Bad("Search is too long.");
        }

        if (view == "trash")
        {
            return await db.Query("""
                SELECT e.*, 'owner' AS role, COALESCE(v.size,0) AS size FROM entries e
                LEFT JOIN LATERAL (SELECT size FROM versions WHERE entry_id=e.id ORDER BY selected_at DESC,id DESC LIMIT 1) v ON true
                WHERE e.owner_id=$1 AND e.trashed_at IS NOT NULL
                ORDER BY e.trashed_at DESC,e.id LIMIT 101 OFFSET $2
                """, user, offset);
        }
        if (view == "shared")
        {
            return await db.Query(Columns + " WHERE g.user_id=$1 ORDER BY lower(e.name),e.id LIMIT 101 OFFSET $2", user, offset);
        }

        await Access.Parent(db, parent, user);
        return await db.Query(Columns + """
             WHERE e.owner_id=$1 AND ($3<>'' OR e.parent_id IS NOT DISTINCT FROM $2::uuid)
               AND ($3='' OR strpos(lower(e.name),$3)>0)
             ORDER BY (e.kind='folder') DESC, lower(e.name),e.id LIMIT 101 OFFSET $4
            """, user, parent, term, offset);
    }

    public async Task<object> Stats(Guid user)
    {
        await using var db = await database.Open();
        return (await db.One("""
            SELECT u.email,u.quota,
              (SELECT COALESCE(sum(v.size),0)::bigint FROM versions v JOIN entries e ON e.id=v.entry_id WHERE e.owner_id=u.id) AS used,
              (SELECT COALESCE(sum(size),0)::bigint FROM uploads WHERE owner_id=u.id AND state IN ('active','assembling')) AS reserved
            FROM users u WHERE u.id=$1
            """, user))!;
    }

    public async Task<object> CreateFolder(Guid user, FolderRequest request)
    {
        var name = Input.Name(request.Name);
        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        await Access.Parent(db, request.ParentId, user);
        var id = Guid.NewGuid();
        await db.Execute("INSERT INTO entries(id,owner_id,parent_id,name,kind) VALUES($1,$2,$3,$4,'folder')", id, user, request.ParentId, name);
        await db.Commit();
        return new
        {
            id
        };
    }

    public async Task<object> Detail(Guid user, Guid id)
    {
        await using var db = await database.Open();
        return await Access.Entry(db, id, user);
    }

    public async Task Change(Guid user, Guid id, ChangeRequest request)
    {
        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        await Access.Entry(db, id, user, owner: true);
        await Access.Parent(db, request.ParentId, user);
        if (request.ParentId is { } parent)
        {
            var cycle = await db.One("""
                WITH RECURSIVE ancestors AS (
                    SELECT id,parent_id FROM entries WHERE id=$1
                    UNION ALL SELECT e.id,e.parent_id FROM entries e JOIN ancestors a ON e.id=a.parent_id
                ) SELECT id FROM ancestors WHERE id=$2
                """, parent, id);
            if (cycle is not null)
            {
                throw ApiError.Bad("A folder cannot be moved inside itself.");
            }
            // Moving a subtree must also preserve the maximum path depth.
            var height = await db.One("""
                WITH RECURSIVE descendants AS (
                    SELECT id,1 AS depth FROM entries WHERE id=$1
                    UNION ALL SELECT e.id,d.depth+1 FROM entries e JOIN descendants d ON e.parent_id=d.id
                ), ancestors AS (
                    SELECT id,parent_id,1 AS depth FROM entries WHERE id=$2
                    UNION ALL SELECT e.id,e.parent_id,a.depth+1 FROM entries e JOIN ancestors a ON e.id=a.parent_id
                ) SELECT (SELECT max(depth) FROM descendants)+(SELECT max(depth) FROM ancestors) AS depth
                """, id, parent);
            if (height!.Number("depth") > 64)
            {
                throw ApiError.Bad("The move would exceed the folder depth limit.");
            }
        }
        await db.Execute("UPDATE entries SET name=$2,parent_id=$3,updated_at=now() WHERE id=$1", id, Input.Name(request.Name), request.ParentId);
        await db.Commit();
    }

    public async Task Trash(Guid user, Guid id, bool restore)
    {
        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        var entry = await Access.Entry(db, id, user, owner: true, includeTrash: restore);
        if (restore)
        {
            await Access.Parent(db, entry.OptionalId("parent_id"), user);
        }

        await db.Execute(restore ? "UPDATE entries SET trashed_at=NULL,updated_at=now() WHERE id=$1" : "UPDATE entries SET trashed_at=now(),updated_at=now() WHERE id=$1", id);
        await db.Commit();
    }

    public async Task Delete(Guid user, Guid id)
    {
        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        var entry = await Access.Entry(db, id, user, owner: true, includeTrash: true);
        if (entry["trashed_at"] is null)
        {
            throw ApiError.Bad("Move the item to Trash before deleting it permanently.");
        }
        // Queue S3 deletion in the same transaction that removes the metadata.
        await db.Execute("""
            WITH RECURSIVE subtree AS (
              SELECT id FROM entries WHERE id=$1
              UNION ALL SELECT e.id FROM entries e JOIN subtree s ON e.parent_id=s.id
            ) INSERT INTO object_deletions(object_key) SELECT object_key FROM versions WHERE entry_id IN (SELECT id FROM subtree)
            ON CONFLICT DO NOTHING
            """, id);
        var ids = (await db.Query("""
            WITH RECURSIVE subtree AS (SELECT id FROM entries WHERE id=$1 UNION ALL SELECT e.id FROM entries e JOIN subtree s ON e.parent_id=s.id)
            SELECT id FROM subtree
            """, id)).Select(r => r.Id("id")).ToArray();
        // Active uploads reserve their destination until they finish or are cancelled.
        if (await db.One("SELECT id FROM uploads WHERE (entry_id=ANY($1) OR parent_id=ANY($1)) AND state IN ('active','assembling') LIMIT 1", ids) is not null)
        {
            throw new ApiError(409, "Cancel uploads targeting this folder or file before deleting it.");
        }

        await db.Execute("UPDATE uploads SET entry_id=NULL,parent_id=NULL,result_entry_id=NULL WHERE entry_id=ANY($1) OR parent_id=ANY($1) OR result_entry_id=ANY($1)", ids);
        await db.Execute("DELETE FROM shares WHERE entry_id=ANY($1)", ids);
        await db.Execute("DELETE FROM grants WHERE entry_id=ANY($1)", ids);
        await db.Execute("DELETE FROM versions WHERE entry_id=ANY($1)", ids);
        await db.Execute("DELETE FROM entries WHERE id=ANY($1)", ids);
        await db.Commit();
    }

    public async Task<object> Versions(Guid user, Guid id)
    {
        await using var db = await database.Open();
        await Access.Entry(db, id, user);
        return await db.Query("""
            SELECT v.id,v.size,v.sha256,v.content_type,v.created_at,u.email AS created_by
            FROM versions v JOIN users u ON u.id=v.created_by WHERE entry_id=$1 ORDER BY v.selected_at DESC,v.id DESC
            """, id);
    }

    public async Task RestoreVersion(Guid user, Guid id, Guid versionId)
    {
        await using var db = await database.Open();
        await db.Begin();
        var entry = await Access.Entry(db, id, user, edit: true);
        await Access.LockOwner(db, entry.Id("owner_id"));
        await Access.Entry(db, id, user, edit: true);
        var version = await db.One("SELECT * FROM versions WHERE id=$1 AND entry_id=$2", versionId, id) ?? throw ApiError.Missing();
        // Reordering an existing version restores it without duplicating S3 data or quota usage.
        await db.Execute("UPDATE versions SET selected_at=clock_timestamp() WHERE id=$1", versionId);
        await db.Execute("UPDATE entries SET updated_at=now() WHERE id=$1", id);
        await db.Commit();
    }

    public async Task<string> Download(Guid user, Guid id, Guid? versionId)
    {
        await using var db = await database.Open();
        var entry = await Access.Entry(db, id, user);
        var version = await db.One("""
            SELECT * FROM versions WHERE entry_id=$1 AND ($2::uuid IS NULL OR id=$2)
            ORDER BY selected_at DESC,id DESC LIMIT 1
            """, id, versionId) ?? throw ApiError.Missing();
        return await storage.Download(version.Text("object_key"), entry.Text("name"));
    }

    public async Task<object> Shares(Guid user, Guid id)
    {
        await using var db = await database.Open();
        await Access.Entry(db, id, user, owner: true);
        return new
        {
            links = await db.Query("SELECT id,expires_at,created_at FROM shares WHERE entry_id=$1 ORDER BY created_at DESC", id),
            grants = await db.Query("SELECT u.email,g.role FROM grants g JOIN users u ON u.id=g.user_id WHERE entry_id=$1", id)
        };
    }

    public async Task<object> Share(Guid user, Guid id, ShareRequest request)
    {
        if (request.Hours is < 1 or > 720)
        {
            throw ApiError.Bad("Links may last from 1 hour to 30 days.");
        }

        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        var entry = await Access.Entry(db, id, user, owner: true);
        if (entry.Text("kind") != "file")
        {
            throw ApiError.Bad("Share individual files.");
        }

        if ((await db.Query("SELECT id FROM shares WHERE entry_id=$1 AND expires_at>now()", id)).Count >= 20)
        {
            throw ApiError.Bad("Revoke an existing link before creating another.");
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = DateTime.UtcNow.AddHours(request.Hours);
        await db.Execute("INSERT INTO shares(id,entry_id,token_hash,expires_at) VALUES($1,$2,$3,$4)", Guid.NewGuid(), id, Access.TokenHash(token), expires);
        await db.Commit();
        return new
        {
            token,
            expiresAt = expires
        };
    }

    public async Task Grant(Guid user, Guid id, GrantRequest request)
    {
        if (request.Role is not ("viewer" or "editor" or "remove"))
        {
            throw ApiError.Bad("Choose viewer or editor.");
        }

        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        var entry = await Access.Entry(db, id, user, owner: true);
        if (entry.Text("kind") != "file")
        {
            throw ApiError.Bad("Share individual files.");
        }

        var target = await db.One("SELECT id FROM users WHERE email=$1", Input.Email(request.Email)) ?? throw ApiError.Bad("This email has not registered yet.");
        if (target.Id("id") == user)
        {
            throw ApiError.Bad("You already own this file.");
        }

        if (request.Role == "remove")
        {
            await db.Execute("DELETE FROM grants WHERE entry_id=$1 AND user_id=$2", id, target.Id("id"));
        }
        else
        {
            await db.Execute("INSERT INTO grants(entry_id,user_id,role) VALUES($1,$2,$3) ON CONFLICT(entry_id,user_id) DO UPDATE SET role=excluded.role", id, target.Id("id"), request.Role);
        }

        await db.Commit();
    }

    public async Task Revoke(Guid user, Guid id, Guid share)
    {
        await using var db = await database.Open();
        await db.Begin();
        await Access.LockOwner(db, user);
        await Access.Entry(db, id, user, owner: true);
        await db.Execute("DELETE FROM shares WHERE id=$1 AND entry_id=$2", share, id);
        await db.Commit();
    }

    public async Task<object> PublicShare(string token, bool download)
    {
        if (token.Length != 64)
        {
            throw ApiError.Missing();
        }

        await using var db = await database.Open();
        var entry = await db.One("""
            SELECT e.name,v.size,v.sha256,v.object_key,s.expires_at FROM shares s
            JOIN live_entries e ON e.id=s.entry_id
            JOIN LATERAL (SELECT * FROM versions WHERE entry_id=e.id ORDER BY selected_at DESC,id DESC LIMIT 1) v ON true
            WHERE s.token_hash=$1 AND s.expires_at>now()
            """, Access.TokenHash(token)) ?? throw ApiError.Missing();
        if (download)
        {
            return await storage.Download(entry.Text("object_key"), entry.Text("name"));
        }

        entry.Remove("object_key");
        return entry;
    }
}

public record FolderRequest(string Name, Guid? ParentId);
public record ChangeRequest(string Name, Guid? ParentId);
public record ShareRequest(int Hours);
public record GrantRequest(string Email, string Role);
