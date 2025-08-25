# azure-storage-demo-populator

A small console app that continuously generates **dummy activity** across Azure Storage accounts.

It creates random containers, tables, queues, and file shares, then uploads blobs, inserts rows, and pushes queue messages in bursts.  
Useful for demos, exploring Azure Storage Discovery, testing throttling, and making empty accounts look production‑like.

See more: https://zimmergren.net/how-to-populate-azure-storage-accounts-with-demo-data/

## Prerequisites

- .NET 8 SDK (or newer)
- One or more Azure Storage accounts with Access Keys enabled
- Connection strings for 1 or more demo accounts (you will paste these into the code, use with caution, and only dummy accounts)

## Configuration

Open `Program.cs` and populate the `StorageConnectionStrings` array with your storage account connection strings.  
For example, provide 3–5 entries to spread load across multiple accounts across regions for a more realistic output.

Example:

```csharp
private static readonly string[] StorageConnectionStrings = new[]
{
    "<storage-connstring-1>",
    "<storage-connstring-2>",
    "<storage-connstring-3>"
    // "<storage-connstring-4>",
    // "<storage-connstring-5>",
};
```

## Usage

1. Clone the repo

   ```bash
   git clone https://github.com/Zimmergren/azure-storage-demo-populator.git
   cd azure-storage-demo-populator
   ```

2. Build the project (restores NuGet packages automatically)

   ```bash
   dotnet build
   ```

3. Run the tool

   Default mode (random speed with natural bursts):
   ```bash
   dotnet run
   ```

   Specific speed profile:
   ```bash
   dotnet run slow
   dotnet run medium
   dotnet run fastest
   ```

   What it does:
   - Rotates between your configured storage accounts about once per minute
   - Randomly selects a container, table, queue, or file share
   - Creates many resources up front (20–50 per type) with random names
   - Populates them with thousands of blobs, rows, messages, or files in parallel
   - Continues indefinitely until you stop it
   - Can add multi-million transactions in a short time, so consider how long you want to leave it running and at what speed.

## Cleanup

This will generate thousands or millions of transactions and objects.  
The simplest cleanup is to delete the demo storage account(s) in the Azure Portal.

See more: [How to populate Azure Storage Accounts with demo data](https://zimmergren.net/how-to-populate-azure-storage-accounts-with-demo-data/)
