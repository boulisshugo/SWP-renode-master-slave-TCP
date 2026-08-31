#!/usr/bin/env bash
# Plain javac — no build tool and no downloads, so it works anywhere a JDK does.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out
javac -d out $(find src -name '*.java')
echo "built: run with  java -cp out swp.Main [host] [eventPort] [linkPort]"
