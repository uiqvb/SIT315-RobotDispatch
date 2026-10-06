#!/bin/bash
watch -n 5 'grep "^===" ~/grid.log | tail -1; echo; tail -6 $(ls -td ~/results/*/ | head -1)db_check.csv | cut -d, -f1,2,6,7'
