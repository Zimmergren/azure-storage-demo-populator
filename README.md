# azure-storage-demo-populator

A small console app that continuously generates **dummy activity** across Azure Storage accounts.

It creates random containers, tables, queues, and file shares, then uploads blobs, inserts rows, and pushes queue messages in bursts.  
Useful for demos, exploring Azure Storage Discovery, testing throttling, and making empty accounts look production‑like.

See more: https://zimmergren.net/how-to-populate-azure-storage-accounts-with-demo-data/

<img width="2184" height="1358" alt="image" src="https://github.com/user-attachments/assets/73ad285f-328b-41da-8edf-4d1b9e66a55f" />


## Prerequisites

- .NET 8 SDK (or newer)
- One or more Azure Storage accounts
- For managed identity: Azure VM with managed identity assigned and Storage Blob Data Contributor role on target storage accounts
- For connection strings: Access keys enabled on storage accounts (use only with dummy/demo accounts)

## Configuration

The application uses a JSON configuration file to specify authentication method and storage accounts. On first run, it creates a default `config.json` file.

### Managed Identity (Recommended for Azure VMs)

Default configuration for Azure VMs with managed identity:

```json
{
  "authMode": "ManagedIdentity",
  "storageAccounts": [
    {
      "name": "storageaccount1",
      "connectionString": null
    },
    {
      "name": "storageaccount2",
      "connectionString": null
    }
  ]
}
```

### Connection Strings

For local development or environments without managed identity:

```json
{
  "authMode": "ConnectionString", 
  "storageAccounts": [
    {
      "name": "storageaccount1",
      "connectionString": "DefaultEndpointsProtocol=https;AccountName=account1;AccountKey=key1;EndpointSuffix=core.windows.net"
    },
    {
      "name": "storageaccount2", 
      "connectionString": "DefaultEndpointsProtocol=https;AccountName=account2;AccountKey=key2;EndpointSuffix=core.windows.net"
    }
  ]
}
```

Provide 3–5 storage accounts to spread load across multiple accounts/regions for realistic output.

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

3. Configure storage accounts

   On first run, the application creates a default `config.json` file. Edit this file with your storage account details.

4. Run the tool

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

   Custom configuration file:
   ```bash
   dotnet run custom-config.json slow
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
