using System.Text;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Azure.Storage.Queues;

public class Program
{
    // All resources created by this tool use this prefix so they can be identified and cleaned up.
    private const string ResourcePrefix = "demo";

    // Cap concurrent Azure SDK calls to avoid socket/thread-pool exhaustion.
    private static readonly SemaphoreSlim Throttle = new(200, 200);

    // Per-account context holding resource pools
    private class StorageContext
    {
        public string AccountLabel = default!;
        public BlobServiceClient BlobService = default!;
        public TableServiceClient TableService = default!;
        public QueueServiceClient QueueService = default!;
        public ShareServiceClient ShareService = default!;
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
    private static readonly CancellationTokenSource _cts = new();

    public static async Task Main(string[] args)
    {
        // Wire up graceful shutdown on Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested. Finishing current batch...");
            _cts.Cancel();
        };

        // Parse arguments
        string speedArg = "random";
        var accountNames = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--speed" && i + 1 < args.Length)
            {
                speedArg = args[++i].Trim().ToLowerInvariant();
            }
            else if (args[i] is "--accounts")
            {
                for (int j = i + 1; j < args.Length && !args[j].StartsWith("--"); j++)
                {
                    accountNames.Add(args[j].Trim());
                    i = j;
                }
            }

        }

        // Legacy: bare first arg as speed (e.g. "dotnet run slow")
        if (args.Length > 0 && !args[0].StartsWith("--") && accountNames.Count == 0)
            speedArg = args[0].Trim().ToLowerInvariant();

        // Validate account names: must be 3-24 chars, lowercase alphanumeric only (Azure rule)
        var validAccountName = new Regex(@"^[a-z0-9]{3,24}$");
        foreach (var name in accountNames)
        {
            if (!validAccountName.IsMatch(name))
            {
                Console.WriteLine($"Invalid storage account name: '{name}'. Must be 3-24 lowercase alphanumeric characters.");
                return;
            }
        }

        Console.WriteLine($"Speed mode: {speedArg}");

        // Build contexts using Managed Identity (DefaultAzureCredential)
        if (accountNames.Count == 0)
        {
            Console.WriteLine("No storage accounts configured.");
            Console.WriteLine("  Use --accounts <name1> <name2> ... to specify target storage accounts.");
            return;
        }

        var credential = new DefaultAzureCredential();
        foreach (var name in accountNames)
        {
            var blobUri = new Uri($"https://{name}.blob.core.windows.net");
            var tableUri = new Uri($"https://{name}.table.core.windows.net");
            var queueUri = new Uri($"https://{name}.queue.core.windows.net");
            var shareUri = new Uri($"https://{name}.file.core.windows.net");

            // Azure Files OAuth requires ShareTokenIntent.Backup
            var shareOptions = new ShareClientOptions { ShareTokenIntent = ShareTokenIntent.Backup };

            _accounts.Add(new StorageContext
            {
                AccountLabel = name,
                BlobService = new BlobServiceClient(blobUri, credential),
                TableService = new TableServiceClient(tableUri, credential),
                QueueService = new QueueServiceClient(queueUri, credential),
                ShareService = new ShareServiceClient(shareUri, credential, shareOptions),
            });
        }
        Console.WriteLine($"Using DefaultAzureCredential for {accountNames.Count} account(s).");

        // Create random-named resources per account
        Console.WriteLine("Initializing resources...");
        var initTasks = new List<Task>();
        foreach (var acct in _accounts)
            initTasks.Add(InitResourcesForAccount(acct));
        await Task.WhenAll(initTasks);
        Console.WriteLine("Initialization complete.");

        _startUtc = DateTime.UtcNow;

        // Main loop: choose operation type and run a big parallel batch
        while (!_cts.Token.IsCancellationRequested)
        {
            var profile = ChooseProfile(speedArg);

            // Switch target account every minute (UTC)
            var index = (int)((DateTime.UtcNow - _startUtc).TotalMinutes) % _accounts.Count;
            var ctx = _accounts[index];

            int op = Random.Shared.Next(4);
            try
            {
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Azure.RequestFailedException ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] ERROR ({ex.Status}): {ex.Message.Split('\n')[0]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] ERROR: {ex.Message.Split('\n')[0]}");
            }

            // Delay with jitter; occasionally "pause less" for bursty feel
            int delay = Random.Shared.Next(profile.DelayBetweenBursts.minMs, profile.DelayBetweenBursts.maxMs);
            if (Random.Shared.NextDouble() < 0.15) delay = (int)(delay * 0.4);
            try { await Task.Delay(delay, _cts.Token); } catch (OperationCanceledException) { break; }
        }

        Console.WriteLine("Stopped.");
    }

    private static int NextCount((int min, int max) baseRange)
    {
        int value = Random.Shared.Next(baseRange.min, baseRange.max + 1);

        // 10% chance to scale up 2-4x (mega burst)
        if (Random.Shared.NextDouble() < 0.10)
        {
            double mult = 2 + Random.Shared.NextDouble() * 2;
            value = (int)(value * mult);
        }

        return value;
    }

    private static SpeedProfile ChooseProfile(string mode)
    {
        if (mode == "slow") return Slow;
        if (mode == "medium") return Medium;
        if (mode == "fastest") return Fastest;
        double r = Random.Shared.NextDouble();
        if (r < 0.25) return Slow;
        if (r < 0.60) return Medium;
        return Fastest;
    }

    private static async Task InitResourcesForAccount(StorageContext acct)
    {
        int n = Random.Shared.Next(20, 51);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            string suffix = Guid.NewGuid().ToString("N")[..8];

            var container = acct.BlobService.GetBlobContainerClient($"{ResourcePrefix}-c{suffix}");
            tasks.Add(container.CreateIfNotExistsAsync());
            acct.Containers.Add(container);

            // Table names cannot contain hyphens
            var table = acct.TableService.GetTableClient($"{ResourcePrefix}t{suffix}");
            tasks.Add(table.CreateIfNotExistsAsync());
            acct.Tables.Add(table);

            var queue = acct.QueueService.GetQueueClient($"{ResourcePrefix}-q{suffix}");
            tasks.Add(queue.CreateIfNotExistsAsync());
            acct.Queues.Add(queue);

            var share = acct.ShareService.GetShareClient($"{ResourcePrefix}-s{suffix}");
            tasks.Add(share.CreateIfNotExistsAsync());
            acct.Shares.Add(share);
        }

        await Task.WhenAll(tasks);
        Console.WriteLine($"[{acct.AccountLabel}] Ready: {acct.Containers.Count} containers, {acct.Tables.Count} tables, {acct.Queues.Count} queues, {acct.Shares.Count} shares.");
    }

    private static async Task ThrottledRun(Func<Task> work)
    {
        await Throttle.WaitAsync(_cts.Token);
        try { await work(); }
        finally { Throttle.Release(); }
    }

    private static async Task UploadBlobs(StorageContext acct, int count)
    {
        var container = acct.Containers[Random.Shared.Next(acct.Containers.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Blobs: {count} => {container.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(ThrottledRun(async () =>
            {
                var blob = container.GetBlobClient($"{ResourcePrefix}-blob-{Guid.NewGuid():N}.txt");
                var content = Encoding.UTF8.GetBytes($"Blob {Guid.NewGuid()} @ {DateTime.UtcNow:o}");
                using var ms = new MemoryStream(content);
                await blob.UploadAsync(ms, overwrite: true);
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task InsertTableRows(StorageContext acct, int count)
    {
        var table = acct.Tables[Random.Shared.Next(acct.Tables.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Table: {count} => {table.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(ThrottledRun(() =>
            {
                var entity = new TableEntity(
                    partitionKey: $"{ResourcePrefix}-p{Random.Shared.Next(1, 101)}",
                    rowKey: Guid.NewGuid().ToString("N"))
                {
                    { "Msg", $"Hi {Guid.NewGuid()}" },
                    { "TsUtc", DateTime.UtcNow },
                    { "Shard", Random.Shared.Next(0, 1024) }
                };
                return table.AddEntityAsync(entity);
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task AddQueueMessages(StorageContext acct, int count)
    {
        var queue = acct.Queues[Random.Shared.Next(acct.Queues.Count)];
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Queue: {count} => {queue.Name}");
        var tasks = new List<Task>(capacity: count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(ThrottledRun(() =>
            {
                var msg = $"Message {Guid.NewGuid()} @ {DateTime.UtcNow:o}";
                return queue.SendMessageAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(msg)));
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task UploadFiles(StorageContext acct, int count)
    {
        var share = acct.Shares[Random.Shared.Next(acct.Shares.Count)];
        var root = share.GetRootDirectoryClient();

        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Files: {count} => {share.Name}");
        var tasks = new List<Task>(count);

        for (int i = 0; i < count; i++)
        {
            tasks.Add(ThrottledRun(async () =>
            {
                var fileName = $"{ResourcePrefix}-file-{Guid.NewGuid():N}.txt";
                var file = root.GetFileClient(fileName);
                var bytes = Encoding.UTF8.GetBytes($"File {Guid.NewGuid()} @ {DateTime.UtcNow:o}");

                await ExecuteWithRetries(async () =>
                {
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
                ex.Status == 404 ||
                ex.Status == 409 ||
                ex.Status == 503 || ex.Status == 500 || ex.Status == 429)
            {
                if (attempt == maxRetries) throw;
                await Task.Delay(delayMs);
                delayMs = Math.Min(delayMs * 2, 4000);
            }
        }
    }
}
