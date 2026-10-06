#!/bin/bash
POOL=50 ./grid.sh > ~/grid.log 2>&1
POOL=50 TAG=stress SETUPS="old/1 new/2" ROBOTS="2000,5000" REPEATS=1 BACKLOG=200 LIMIT=600 ./stress.sh > ~/stress.log 2>&1
echo ALL_DONE >> ~/stress.log
