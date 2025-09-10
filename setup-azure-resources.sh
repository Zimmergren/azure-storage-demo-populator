#!/bin/bash

# Azure Storage Demo Populator - Resource Setup Script
# This script creates a resource group, two storage accounts, and assigns managed identity permissions

set -e  # Exit on any error

# Configuration variables
RESOURCE_GROUP="rg-storage-demo"
LOCATION="eastus2"
STORAGE_ACCOUNT_1="storagedemoacct1$(date +%s | tail -c 6)"
STORAGE_ACCOUNT_2="storagedemoacct2$(date +%s | tail -c 6)"
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

echo "=========================================="
echo "Azure Storage Demo Populator Setup Script"
echo "=========================================="
echo "Resource Group: $RESOURCE_GROUP"
echo "Location: $LOCATION"
echo "Storage Account 1: $STORAGE_ACCOUNT_1"
echo "Storage Account 2: $STORAGE_ACCOUNT_2"
echo "Subscription: $SUBSCRIPTION_ID"
echo "=========================================="

# Step 1: Create Resource Group
echo "Step 1: Creating resource group '$RESOURCE_GROUP' in '$LOCATION'..."
az group create \
    --name "$RESOURCE_GROUP" \
    --location "$LOCATION"
echo "✓ Resource group created successfully"

# Step 2: Create Storage Account 1
echo ""
echo "Step 2: Creating storage account '$STORAGE_ACCOUNT_1'..."
az storage account create \
    --name "$STORAGE_ACCOUNT_1" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku "Standard_LRS" \
    --kind "StorageV2" \
    --access-tier "Hot" \
    --allow-blob-public-access false \
    --min-tls-version "TLS1_2"
echo "✓ Storage account 1 created successfully"

# Step 3: Create Storage Account 2
echo ""
echo "Step 3: Creating storage account '$STORAGE_ACCOUNT_2'..."
az storage account create \
    --name "$STORAGE_ACCOUNT_2" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku "Standard_LRS" \
    --kind "StorageV2" \
    --access-tier "Hot" \
    --allow-blob-public-access false \
    --min-tls-version "TLS1_2"
echo "✓ Storage account 2 created successfully"

# Step 4: Get the current VM's managed identity principal ID
echo ""
echo "Step 4: Getting current VM's managed identity..."

# Function to decode JWT payload and extract OID
decode_jwt_oid() {
    local token="$1"
    # Split JWT token and get payload (second part)
    local payload=$(echo "$token" | cut -d'.' -f2)
    
    # Add padding for base64 decoding if needed
    local padding=$((4 - ${#payload} % 4))
    if [ $padding -lt 4 ]; then
        payload="${payload}$(printf '=%.0s' $(seq 1 $padding))"
    fi
    
    # Decode base64 and extract oid using python
    echo "$payload" | python3 -c "
import base64
import json
import sys
try:
    data = base64.b64decode(sys.stdin.read().strip())
    payload = json.loads(data.decode('utf-8'))
    print(payload.get('oid', ''))
except:
    pass
"
}

# Try multiple methods to get the managed identity principal ID
PRINCIPAL_ID=""

# Method 1: Try using Azure CLI to get current identity
if command -v az >/dev/null 2>&1; then
    echo "  Trying Azure CLI method..."
    PRINCIPAL_ID=$(az account show --query user.name -o tsv 2>/dev/null | grep -E '^[0-9a-f-]{36}$' || echo "")
fi

# Method 2: Try using managed identity metadata endpoint
if [ -z "$PRINCIPAL_ID" ]; then
    echo "  Trying managed identity metadata endpoint..."
    ACCESS_TOKEN=$(curl -s --connect-timeout 5 'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fmanagement.azure.com%2F' -H Metadata:true 2>/dev/null | jq -r '.access_token // empty' 2>/dev/null)
    if [ ! -z "$ACCESS_TOKEN" ] && [ "$ACCESS_TOKEN" != "null" ]; then
        PRINCIPAL_ID=$(decode_jwt_oid "$ACCESS_TOKEN")
    fi
fi

# Method 3: Try using Azure Instance Metadata Service for VM identity
if [ -z "$PRINCIPAL_ID" ]; then
    echo "  Trying Azure Instance Metadata Service..."
    VM_IDENTITY=$(curl -s --connect-timeout 5 'http://169.254.169.254/metadata/identity/info?api-version=2021-02-01' -H Metadata:true 2>/dev/null)
    if [ ! -z "$VM_IDENTITY" ]; then
        PRINCIPAL_ID=$(echo "$VM_IDENTITY" | jq -r '.principalId // empty' 2>/dev/null)
    fi
fi

if [ -z "$PRINCIPAL_ID" ]; then
    echo "⚠️  Could not automatically detect managed identity principal ID."
    echo "This could be due to:"
    echo "  - Running outside of an Azure VM with managed identity enabled"
    echo "  - Missing required tools (jq, python3, curl)"
    echo "  - Network connectivity issues"
    echo ""
    echo "Manual permission assignment required (see end of script output)."
else
    echo "✓ Managed identity principal ID: $PRINCIPAL_ID"
fi

# Function to assign role with retry logic
assign_role_with_retry() {
    local assignee="$1"
    local role="$2"
    local scope="$3"
    local max_attempts=3
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        echo "    Attempt $attempt/$max_attempts: Assigning $role..."
        
        if az role assignment create \
            --assignee "$assignee" \
            --role "$role" \
            --scope "$scope" \
            --output none 2>/dev/null; then
            echo "    ✓ Successfully assigned $role"
            return 0
        else
            # Check if assignment already exists
            if az role assignment list \
                --assignee "$assignee" \
                --role "$role" \
                --scope "$scope" \
                --query "[0].principalId" \
                --output tsv 2>/dev/null | grep -q "$assignee"; then
                echo "    ✓ Role $role already assigned"
                return 0
            fi
            
            if [ $attempt -eq $max_attempts ]; then
                echo "    ❌ Failed to assign $role after $max_attempts attempts"
                return 1
            else
                echo "    ⚠️  Failed to assign $role, retrying in 5 seconds..."
                sleep 5
            fi
        fi
        attempt=$((attempt + 1))
    done
}

# Initialize failure counters
FAILED_ASSIGNMENTS_1=0
FAILED_ASSIGNMENTS_2=0
TOTAL_FAILED=0

# Step 5: Assign required permissions to storage account 1
if [ ! -z "$PRINCIPAL_ID" ]; then
    echo ""
    echo "Step 5: Assigning permissions to storage account 1..."
    
    STORAGE_1_SCOPE="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1"
    ROLES=("Storage Blob Data Contributor" "Storage Queue Data Contributor" "Storage Table Data Contributor" "Storage File Data SMB Share Contributor")
    
    for role in "${ROLES[@]}"; do
        if ! assign_role_with_retry "$PRINCIPAL_ID" "$role" "$STORAGE_1_SCOPE"; then
            FAILED_ASSIGNMENTS_1=$((FAILED_ASSIGNMENTS_1 + 1))
        fi
    done
    
    if [ $FAILED_ASSIGNMENTS_1 -eq 0 ]; then
        echo "✓ All permissions assigned to storage account 1"
    else
        echo "⚠️  $FAILED_ASSIGNMENTS_1 permission assignments failed for storage account 1"
    fi

    # Step 6: Assign required permissions to storage account 2
    echo ""
    echo "Step 6: Assigning permissions to storage account 2..."
    
    STORAGE_2_SCOPE="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2"
    
    for role in "${ROLES[@]}"; do
        if ! assign_role_with_retry "$PRINCIPAL_ID" "$role" "$STORAGE_2_SCOPE"; then
            FAILED_ASSIGNMENTS_2=$((FAILED_ASSIGNMENTS_2 + 1))
        fi
    done
    
    if [ $FAILED_ASSIGNMENTS_2 -eq 0 ]; then
        echo "✓ All permissions assigned to storage account 2"
    else
        echo "⚠️  $FAILED_ASSIGNMENTS_2 permission assignments failed for storage account 2"
    fi
    
    # Check overall status
    TOTAL_FAILED=$((FAILED_ASSIGNMENTS_1 + FAILED_ASSIGNMENTS_2))
    if [ $TOTAL_FAILED -gt 0 ]; then
        echo ""
        echo "⚠️  $TOTAL_FAILED role assignments failed. You may need to assign them manually."
    fi
fi

# Step 7: Update the application configuration
echo ""
echo "Step 7: Updating config.json with new storage account names..."
cat > config.json << EOF
{
  "authMode": "ManagedIdentity",
  "storageAccounts": [
    {
      "name": "$STORAGE_ACCOUNT_1",
      "connectionString": null
    },
    {
      "name": "$STORAGE_ACCOUNT_2",
      "connectionString": null
    }
  ]
}
EOF
echo "✓ Configuration file updated"

echo ""
echo "=========================================="
echo "Setup Complete!"
echo "=========================================="
echo "Resource Group: $RESOURCE_GROUP"
echo "Storage Account 1: $STORAGE_ACCOUNT_1"
echo "Storage Account 2: $STORAGE_ACCOUNT_2"
echo ""
echo "The config.json file has been updated with the new storage account names."
echo "You can now run the application with: dotnet run"
echo ""
if [ -z "$PRINCIPAL_ID" ] || [ $TOTAL_FAILED -gt 0 ]; then
    echo "⚠️  IMPORTANT: Managed identity permissions require manual assignment."
    echo ""
    if [ -z "$PRINCIPAL_ID" ]; then
        echo "Could not automatically detect managed identity principal ID."
        echo "To assign permissions manually:"
        echo ""
        echo "1. Get your managed identity principal ID:"
        echo "   az ad sp list --display-name \"\$(hostname)\" --query '[0].id' -o tsv"
        echo "   # OR check in Azure portal: VM -> Identity -> Object (principal) ID"
        echo ""
        echo "2. Assign roles using Azure CLI:"
        echo "   PRINCIPAL_ID=<your-principal-id>"
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Blob Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Queue Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Table Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage File Data SMB Share Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1\""
        echo ""
        echo "   # Repeat for storage account 2:"
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Blob Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Queue Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage Table Data Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2\""
        echo "   az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"Storage File Data SMB Share Contributor\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2\""
    else
        echo "Some role assignments failed. You can retry with:"
        echo ""
        echo "PRINCIPAL_ID=\"$PRINCIPAL_ID\""
        for role in "Storage Blob Data Contributor" "Storage Queue Data Contributor" "Storage Table Data Contributor" "Storage File Data SMB Share Contributor"; do
            echo "az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"$role\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_1\""
            echo "az role assignment create --assignee \"\$PRINCIPAL_ID\" --role \"$role\" --scope \"/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Storage/storageAccounts/$STORAGE_ACCOUNT_2\""
        done
    fi
    echo ""
    echo "3. Alternatively, assign roles via Azure Portal:"
    echo "   Storage Account -> Access Control (IAM) -> Add role assignment"
    echo ""
fi
echo "=========================================="