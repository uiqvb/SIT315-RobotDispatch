#!/bin/bash
# Azure setup part 2: outbound NAT, 3 VMs with Docker, internal Standard load balancer in front of the 2 API VMs.
set -e
export MSYS_NO_PATHCONV=1
AZ="${AZ:-az}"; RG=rg-sit315-bench
"$AZ" network public-ip create -g $RG -n pip-nat --sku Standard -o none
"$AZ" network nat gateway create -g $RG -n nat-bench --public-ip-addresses pip-nat -o none
"$AZ" network vnet subnet update -g $RG --vnet-name vnet-bench -n snet-vms --nat-gateway nat-bench -o none
cat > cloud-init.yaml <<'CI'
#cloud-config
package_update: true
packages: [docker.io]
runcmd:
  - usermod -aG docker azureuser
  - systemctl enable --now docker
CI
VM="--image Ubuntu2404 --admin-username azureuser --ssh-key-values $KEYS --custom-data cloud-init.yaml --vnet-name vnet-bench --subnet snet-vms -o none"
"$AZ" vm create -g $RG -n vm-load  --size Standard_D2ds_v4 --private-ip-address 10.10.1.4 --public-ip-sku Standard --nsg nsg-bench $VM
# API VMs: NIC made first so they get a private IP only (no public IP, no NSG)
VMN="--image Ubuntu2404 --admin-username azureuser --ssh-key-values $KEYS --custom-data cloud-init.yaml -o none"
for n in 1 2; do
  "$AZ" network nic create -g $RG -n vm-api-${n}VMNic --vnet-name vnet-bench --subnet snet-vms --private-ip-address 10.10.1.$((4+n)) -o none
  "$AZ" vm create -g $RG -n vm-api-$n --size Standard_D2s_v4 --nics vm-api-${n}VMNic $VMN
done
"$AZ" network lb create -g $RG -n lb-bench --sku Standard --vnet-name vnet-bench --subnet snet-vms --private-ip-address 10.10.1.100 --frontend-ip-name fe --backend-pool-name pool -o none
"$AZ" network lb probe create -g $RG --lb-name lb-bench -n health --protocol Http --port 8080 --path /health --interval 5 -o none
"$AZ" network lb rule create -g $RG --lb-name lb-bench -n http --protocol Tcp --frontend-port 80 --backend-port 8080 --frontend-ip-name fe --backend-pool-name pool --probe-name health -o none
for v in vm-api-1 vm-api-2; do
  "$AZ" network nic ip-config address-pool add -g $RG --nic-name ${v}VMNic --ip-config-name ipconfig1 --lb-name lb-bench --address-pool pool -o none
done
