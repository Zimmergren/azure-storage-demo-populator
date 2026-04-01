# Azure Storage Demo Populator

A .NET console app that continuously generates real, but **dummy activity**, across one or more Azure Storage accounts. It creates random containers, tables, queues, and file shares, then uploads blobs, inserts table rows, pushes queue messages, and writes files in parallel bursts.

Useful for demos, exploring Azure Storage Discovery, testing throttling behavior, and making empty accounts look production-like.

> See the companion blog post: [How to populate Azure Storage Accounts with demo data](https://zimmergren.net/how-to-populate-azure-storage-accounts-with-demo-data/)

<img width="2184" height="1358" alt="Azure Storage Demo Populator screenshot" src="https://github.com/user-attachments/assets/73ad285f-328b-41da-8edf-4d1b9e66a55f" />

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download) (or newer)
- One or more Azure Storage accounts

## Authentication

Uses `DefaultAzureCredential` from the Azure Identity SDK. This works with Managed Identity, Azure CLI, Visual Studio, and other credential sources — no secrets required.

Provide storage account names as arguments:

```bash
dotnet run -- --accounts mystorageaccount1 mystorageaccount2 mystorageaccount3
```

The tool connects to each account via `https://{name}.blob.core.windows.net` (and the corresponding table, queue, and file endpoints).

> **Note:** The signed-in identity needs the following data-plane roles on each storage account:
> - **Storage Blob Data Contributor** (blob containers & blobs)
> - **Storage Table Data Contributor** (tables & entities)
> - **Storage Queue Data Contributor** (queues & messages)
> - **Storage File Data Privileged Contributor** (file shares & files — required for OAuth with `ShareTokenIntent.Backup`)

## Usage

1. **Clone the repo**

   ```bash
   git clone https://github.com/Zimmergren/azure-storage-demo-populator.git
   cd azure-storage-demo-populator
   ```

2. **Build**

   ```bash
   dotnet build
   ```

3. **Run**

   ```bash
   dotnet run -- --accounts storageaccount1 storageaccount2
   ```

   With a speed profile:
   ```bash
   dotnet run -- --speed medium --accounts storageaccount1 storageaccount2
   ```

### Speed profiles

| Profile    | Description |
|------------|-------------|
| `slow`     | Small batches (20–80 items), longer pauses |
| `medium`   | Moderate batches (200–800 items) |
| `fastest`  | Large batches (800–2500 items), minimal pauses |
| `random`   | *(default)* Randomly switches between profiles to simulate organic load |

### What it does

- Creates 20–50 randomly named containers, tables, queues, and file shares per account on startup
- Rotates between configured storage accounts approximately once per minute
- Randomly selects an operation type (blob upload, table insert, queue message, file write)
- Runs operations in parallel with occasional "mega-burst" spikes (2–4× normal volume)
- Continues indefinitely until stopped (`Ctrl+C`)
- Can generate multi-million transactions quickly — consider how long you leave it running

## Resource naming

All resources created by this tool are prefixed with `demo-` (or `demot` for tables, which don't allow hyphens):

| Resource type | Name pattern | Example |
|---------------|-------------|--------|
| Blob container | `demo-c{8hex}` | `demo-c4a8f1b2e` |
| Table | `demot{8hex}` | `demot4a8f1b2e` |
| Queue | `demo-q{8hex}` | `demo-q4a8f1b2e` |
| File share | `demo-s{8hex}` | `demo-s4a8f1b2e` |
| Blobs | `demo-blob-{guid}.txt` | |
| Files | `demo-file-{guid}.txt` | |
| Table rows | partition key `demo-p{1-100}` | |

This makes it easy to identify and selectively delete demo data.

## Cleanup

All resources are prefixed with `demo-` (or `demot` for tables), making them easy to identify and delete manually in the Azure Portal or via scripts. Alternatively, delete the demo storage account(s) entirely.