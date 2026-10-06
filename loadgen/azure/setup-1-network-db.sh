#!/bin/bash
# Azure setup part 1: resource group, network, PostgreSQL, container registry (Azure for Students, koreacentral).
set -e
export MSYS_NO_PATHCONV=1   # Git Bash on Windows: stop /paths being rewritten to C:/Program Files/Git/...
AZ="${AZ:-az}"; RG=rg-sit315-bench; L=koreacentral
"$AZ" group create -n $RG -l $L -o none
"$AZ" network vnet create -g $RG -n vnet-bench --address-prefixes 10.10.0.0/16 --subnet-name snet-vms --subnet-prefixes 10.10.1.0/24 -o none
"$AZ" network vnet subnet create -g $RG --vnet-name vnet-bench -n snet-pg --address-prefixes 10.10.2.0/24 --delegations Microsoft.DBforPostgreSQL/flexibleServers -o none
"$AZ" network nsg create -g $RG -n nsg-bench -o none
"$AZ" network nsg rule create -g $RG --nsg-name nsg-bench -n ssh-in --priority 100 --access Allow --protocol Tcp --destination-port-ranges 22 --source-address-prefixes "$MYIP/32" -o none
ZONE=$("$AZ" network private-dns zone create -g $RG -n benchdns.private.postgres.database.azure.com --query id -o tsv)
"$AZ" postgres flexible-server create -g $RG -n pg-sit315-bench -l $L --version 18 --tier GeneralPurpose --sku-name Standard_D8ds_v5 --storage-size 128   --admin-user benchadmin --admin-password "$PGPASS" --vnet vnet-bench --subnet snet-pg --private-dns-zone "$ZONE" --yes -o none   # about 10 min
"$AZ" network private-dns link vnet create -g $RG -z benchdns.private.postgres.database.azure.com -n link-vnet-bench -v vnet-bench -e false -o none
"$AZ" postgres flexible-server parameter set -g $RG -s pg-sit315-bench -n require_secure_transport --value off -o none
"$AZ" postgres flexible-server db create -g $RG -s pg-sit315-bench -n robotbench -o none
"$AZ" acr create -g $RG -n acrsit315bench --sku Basic --admin-enabled true -o none
