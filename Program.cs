using System.Text;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Azure.Storage.Queues;

public class Program
{
    // Selecting a subset of storage accounts to populate data into for the sake of demo purposes.
    // Don't use this in production, dev, or test code.
    private static readonly string[] StorageConnectionStrings = new[]
    {
        "<connection string 1>",
        "<connection string 2>",
        "<connection string 3>",
        "<connection string 4>"
    };

    private static readonly Random Rand = new();

    // Per-account context holding resource pools
    private class StorageContext
    {
        public string ConnectionString = default!;
        public List<BlobContainerClient> Containers = new();
        public List<TableClient> Tables = new();
        public List<QueueClient> Queues = new();
        public List<ShareClient> Shares = new();
    }

    // Speed profiles tune batch sizes & pacing
    private record SpeedProfile(
        (int min, int max) Blobs,
        (int min, int max) Tables,
        (int min, int max) Queues,
        (int min, int max) Files,
        (int minMs, int maxMs) DelayBetweenBursts
    );

    private static readonly SpeedProfile Slow = new(
        Blobs: (20, 80),
        Tables: (20, 80),
        Queues: (20, 100),
        Files: (10, 40),
        DelayBetweenBursts: (400, 1200)
    );

    private static readonly SpeedProfile Medium = new(
        Blobs: (200, 800),
        Tables: (200, 800),
        Queues: (200, 1000),
        Files: (150, 600),
        DelayBetweenBursts: (200, 800)
    );

    private static readonly SpeedProfile Fastest = new(
        Blobs: (800, 2500),
        Tables: (800, 2500),
        Queues: (800, 2500),
        Files: (800, 2000),
        DelayBetweenBursts: (80, 300)
    );

    private static List<StorageContext> _accounts = new();
    private static DateTime _startUtc;

    public static async Task Main(string[] args)
    {
        // speed arg: slow | medium | fastest | random (default random)
        string speedArg = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "random";
        Console.WriteLine($"Speed mode: {speedArg}");

        // Build contexts per account
        foreach (var cs in StorageConnectionStrings)
        {
            if (string.IsNullOrWhiteSpace(cs)) continue;
            _accounts.Add(new StorageContext { ConnectionString = cs });
        }

        if (_accounts.Count == 0)
        {
            Console.WriteLine("No storage connection strings configured. Exiting.");
            return;
        }

        // Create random-named resources per account
        Console.WriteLine("Initializing resources...");
        var initTasks = new List<Task>();
        foreach (var acct in _accounts)
            initTasks.Add(InitResourcesForAccount(acct));
        await Task.WhenAll(initTasks);
        Console.WriteLine("Initialization complete.");

        _startUtc = DateTime.UtcNow;

        // Main loop: choose operation type and run a big parallel batch
        while (true)
        {
            var profile = ChooseProfile(speedArg);

            // Switch target account every minute (UTC)
            var index = (int)((DateTime.UtcNow - _startUtc).TotalMinutes) % _accounts.Count;
            var ctx = _accounts[index];

            int op = Rand.Next(4); // 0=blobs,1=tables,2=queues,3=files
            switch (op)
            {
                case 0:
                    await UploadBlobs(ctx, NextCount(profile.Blobs));
                    break;
                case 1:
                    await InsertTableRows(ctx, NextCount(profile.Tables));
                    break;
                case 2:
                    await AddQueueMessages(ctx, NextCount(profile.Queues));
                    break;
                case 3:
                    await UploadFiles(ctx, NextCount(profile.Files));
                    break;
            }

            // Delay with jitter; occasionally “pause less” for bursty feel
            int delay = Rand.Next(profile.DelayBetweenBursts.minMs, profile.DelayBetweenBursts.maxMs);
            if (Rand.NextDouble() < 0.15) delay = (int)(delay * 0.4); // short spike bursts
            await Task.Delay(delay);
        }
    }

    // Randomly ramp counts; sometimes enter “mega-burst”
    private static int NextCount((int min, int max) baseRange)
    {
        // Normal variability
        int value = Rand.Next(baseRange.min, baseRange.max + 1);

        // 10% chance to scale up 2–4x (mega burst)
        if (Rand.NextDouble() < 0.10)
        {
            double mult = 2 + Rand.NextDouble() * 2; // 2–4x
            value = (int)(value * mult);
        }

        return value;
    }

    private static SpeedProfile ChooseProfile(string mode)
    {
        if (mode == "slow") return Slow;
        if (mode == "medium") return Medium;
        if (mode == "fastest") return Fastest;
        // random: sometimes change the overall profile too (to simulate day/night load)
        double r = Rand.NextDouble();
        if (r < 0.25) return Slow;
        if (r < 0.60) return Medium;
        return Fastest;
    }

    private static async Task InitResourcesForAccount(StorageContext acct)
    {
        var blobSvc = new BlobServiceClient(acct.ConnectionString);
        var tableSvc = new TableServiceClient(acct.ConnectionString);
        var queueSvc = new QueueServiceClient(acct.ConnectionString);
        var shareSvc = new ShareServiceClient(acct.ConnectionString);

        int n = Rand.Next(20, 51); // 20–50
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

            // Containers
            var container = blobSvc.GetBlobContainerClient("c" + suffix);
            tasks.Add(container.CreateIfNotExistsAsync());
            acct.Containers.Add(container);

            // Tables
            var table = tableSvc.GetTableClient("t" + suffix);
            tasks.Add(table.CreateIfNotExistsAsync());
            acct.Tables.Add(table);

            // Queues
            var queue = queueSvc.GetQueueClient("q" + suffix);
            tasks.Add(queue.CreateIfNotExistsAsync());
            acct.Queues.Add(queue);

            // File shares
            var share = shareSvc.GetShareClient("s" + suffix);
            tasks.Add(share.CreateIfNotExistsAsync());
            acct.Shares.Add(share);
        }

        await Task.WhenAll(tasks);
        Console.WriteLine($"Account ready: {acct.Containers.Count} containers, {acct.Tables.Count} tables, {acct.Queues.Count} queues, {acct.Shares.Count} shares.");
    }

    private static async Task UploadBlobs(StorageContext acct, int count)
    {
        var container = acct.Containers[Rand.Next(acct.Containers.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Blobs: {count} => {container.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var blob = container.GetBlobClient($"blob-{Guid.NewGuid():N}.txt");
                var content = Encoding.UTF8.GetBytes($"Blob {Guid.NewGuid()} @ {DateTime.UtcNow:o}");
                using var ms = new MemoryStream(content);
                await blob.UploadAsync(ms, overwrite: true);
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task InsertTableRows(StorageContext acct, int count)
    {
        var table = acct.Tables[Rand.Next(acct.Tables.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Table: {count} => {table.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var entity = new TableEntity(
                    partitionKey: "p" + Rand.Next(1, 101).ToString(), // spread partition keys 1..100
                    rowKey: Guid.NewGuid().ToString("N"))
                {
                    { "Msg", $"Hi {Guid.NewGuid()}" },
                    { "TsUtc", DateTime.UtcNow },
                    { "Shard", Rand.Next(0, 1024) }
                };
                return table.AddEntityAsync(entity);
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task AddQueueMessages(StorageContext acct, int count)
    {
        var queue = acct.Queues[Rand.Next(acct.Queues.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Queue: {count} => {queue.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var msg = $"Message {Guid.NewGuid()} @ {DateTime.UtcNow:o}";
                // Base64-encode to be explicit
                return queue.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(msg)));
            }));
        }

        await Task.WhenAll(tasks);
    }


    private static async Task UploadFiles(StorageContext acct, int count)
    {
        var share = acct.Shares[Rand.Next(acct.Shares.Count)];
        var root = share.GetRootDirectoryClient(); // root exists by definition

        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Files: {count} => {share.Name}");
        var tasks = new List<Task>(count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var fileName = $"file-{Guid.NewGuid():N}.txt";
                var file = root.GetFileClient(fileName);
                var bytes = Encoding.UTF8.GetBytes($"File {Guid.NewGuid()} @ {DateTime.UtcNow:o}");

                await ExecuteWithRetries(async () =>
                {
                    // Create+write with a single logical operation
                    var opts = new ShareFileOpenWriteOptions { MaxSize = bytes.Length };
                    await using var stream = await file.OpenWriteAsync(overwrite: true, position: 0, options: opts);
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                });
            }));
        }

        await Task.WhenAll(tasks);
    }

    static async Task ExecuteWithRetries(Func<Task> op, int maxRetries = 5)
    {
        int delayMs = 100;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await op();
                return;
            }
            catch (Azure.RequestFailedException ex) when (
                ex.Status == 404 ||                 // ResourceNotFound (eventual consistency/race)
                ex.Status == 409 ||                 // Conflict/lease-y moments
                ex.Status == 503 || ex.Status == 500 || ex.Status == 429) // transient
            {
                if (attempt == maxRetries) throw;
                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs * 2, 4000);
            }
        }
    }

}