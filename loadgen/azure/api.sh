#!/bin/bash
# usage: api.sh old|new|stop [pool size, default 25]
POOL=${2:-25}
sudo docker rm -f api >/dev/null 2>&1
[ "$1" = stop ] && { echo "$(hostname): stopped"; exit 0; }
sudo docker run -d --name api -p 8080:8080   -e ASPNETCORE_ENVIRONMENT=Production   -e "ConnectionStrings__DefaultConnection=Host=pg-sit315-bench.postgres.database.azure.com;Port=5432;Database=robotbench;Username=benchadmin;Password=${PGPASS};SSL Mode=Disable;Maximum Pool Size=$POOL"   -e DeviceCredential__HashKey=loadgen-bench-hash-key   -e Persistence__Provider=ADO   -e RequestLogging__Enabled=false   -e Logging__LogLevel__Default=Warning   acrsit315bench.azurecr.io/robot-api:$1 >/dev/null
for i in $(seq 1 60); do curl -sf -o /dev/null localhost:8080/health && { echo "$(hostname): $1 healthy after ${i}s (pool $POOL)"; exit 0; }; sleep 1; done
echo "$(hostname): $1 NOT healthy"; sudo docker logs --tail 20 api; exit 1
