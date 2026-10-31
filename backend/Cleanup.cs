namespace Drive;

public sealed class Cleanup(Database database, Storage storage, ILogger<Cleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Sweep();
            }
            catch (Exception error) { logger.LogWarning(error, "Storage cleanup will retry on its next pass."); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task Sweep()
    {
        await using (var db = await database.Open())
        {
            await db.Begin();
            var expired = await db.Query("""
                SELECT * FROM uploads WHERE state IN ('active','failed') AND expires_at<now()
                ORDER BY expires_at LIMIT 20 FOR UPDATE SKIP LOCKED
                """);
            foreach (var upload in expired)
            {
                await Uploads.QueueDeletion(db, upload);
                await db.Execute("UPDATE uploads SET state='expired' WHERE id=$1", upload.Id("id"));
            }
            await db.Execute("DELETE FROM shares WHERE expires_at<now()");
            await db.Commit();
        }
        await using var deletion = await database.Open();
        var pending = await deletion.Query("SELECT * FROM object_deletions ORDER BY created_at LIMIT 30");
        foreach (var item in pending)
        {
            await storage.Delete(item.Text("object_key"), item["multipart_id"] as string);
            await deletion.Execute("DELETE FROM object_deletions WHERE object_key=$1", item.Text("object_key"));
        }
    }
}
