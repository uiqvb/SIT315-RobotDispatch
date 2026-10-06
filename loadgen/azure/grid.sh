#!/bin/bash
# Cloud grid: each setup = API version x replica count, all robot sizes and repeats against the load balancer.
CONN="Host=pg-sit315-bench.postgres.database.azure.com;Port=5432;Database=robotbench;Username=benchadmin;Password=${PGPASS};SSL Mode=Disable;Maximum Pool Size=25"
SETUPS="${SETUPS:-old/1 new/1 new/2 old/2}"
ROBOTS="${ROBOTS:-10,100,400}"
REPEATS="${REPEATS:-3}"
POOL="${POOL:-25}"   # Npgsql Maximum Pool Size per API replica
SSHO="-o StrictHostKeyChecking=accept-new -o BatchMode=yes"
SPEC='echo "$(hostname): $(nproc) vCPU, $(awk "/MemTotal/{printf \"%.1f\", \$2/1048576}" /proc/meminfo) GB, $(grep -m1 "model name" /proc/cpuinfo | sed "s/.*: //")"'
OUT=~/results/grid-$(date -u +%Y%m%d-%H%M%S); mkdir -p $OUT
{
  echo "started (UTC): $(date -u -Iseconds)"
  echo "region: koreacentral (Azure for Students)"
  echo "load VM Standard_D2ds_v4: $(bash -c "$SPEC")"
  for ip in 10.10.1.5 10.10.1.6; do echo "API VM Standard_D2s_v4 $ip: $(ssh $SSHO azureuser@$ip "$SPEC")"; done
  echo "database: Azure Database for PostgreSQL Flexible Server 18, Standard_D8ds_v5, 8 vCore, 128 GB, private subnet, TLS off, Maximum Pool Size=$POOL per replica"
  echo "load balancer: Azure Standard internal LB 10.10.1.100:80 to :8080, HTTP probe /health every 5 s"
  echo "images: robot-api:old = sit315 9faf8b1, robot-api:new = phase-4 a87f92d, robot-loadgen = phase-4 a87f92d"
  echo "parameters: setups $SETUPS, robots $ROBOTS, duration 30 s, warm-up 5 s, backlog 2000 jobs/robot, $REPEATS repeats"
} > $OUT/run-info.txt
cat $OUT/run-info.txt
for setup in $SETUPS; do
  v=${setup%/*}; r=${setup#*/}
  echo "=== $(date -u +%T) setup $v with $r replica(s) ==="
  ssh $SSHO azureuser@10.10.1.5 "~/api.sh $v $POOL"
  if [ "$r" = 2 ]; then ssh $SSHO azureuser@10.10.1.6 "~/api.sh $v $POOL"; else ssh $SSHO azureuser@10.10.1.6 "~/api.sh stop"; fi
  sleep 20   # let the LB probe mark a stopped replica down and a started one up
  sudo docker run --rm -v $OUT:/loadgen/results acrsit315bench.azurecr.io/robot-loadgen:phase-4 bench \
    --api http://10.10.1.100 --db "$CONN" --hash-key loadgen-bench-hash-key \
    --label $v-$r-replica --robots $ROBOTS --backlog 2000 --duration 30 --warmup 5 --repeats $REPEATS \
    --results /loadgen/results --note "Azure koreacentral, $r x D2s_v4 behind Standard LB" 2>&1 | grep -E "DB check|ERR|FAIL|Exception"
done
sudo docker run --rm -v $OUT:/loadgen/results acrsit315bench.azurecr.io/robot-loadgen:phase-4 summary --results /loadgen/results
echo "=== $(date -u +%T) GRID DONE: $OUT ==="
