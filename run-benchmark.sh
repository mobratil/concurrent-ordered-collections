#!/usr/bin/env bash
# Runs the .NET and Java benchmarks back-to-back with identical workloads and
# prints both result sets. Pass --quick for a fast smoke run.
set -euo pipefail

cd "$(dirname "$0")"

# Forward any flags (e.g. --quick, --small) to both harnesses.
QUICK="${*:-}"

# Locate a real JDK. Note: macOS ships a /usr/bin/java *stub* that always exists
# but errors unless a JDK is installed, so `command -v java` is not a reliable
# check. Prefer an explicit JAVA_HOME, then common brew locations, then PATH.
JAVA_BIN=""
if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then
  JAVA_BIN="$JAVA_HOME/bin"
elif [[ -x /opt/homebrew/opt/openjdk/bin/java ]]; then
  JAVA_BIN="/opt/homebrew/opt/openjdk/bin"
elif [[ -x /usr/local/opt/openjdk/bin/java ]]; then
  JAVA_BIN="/usr/local/opt/openjdk/bin"
fi
if [[ -n "$JAVA_BIN" ]]; then export PATH="$JAVA_BIN:$PATH"; fi

echo "=========================================================="
echo " Building .NET benchmark (Release, Server GC)…"
echo "=========================================================="
dotnet build bench/LockFreeSkipList.Bench/LockFreeSkipList.Bench.csproj -c Release -v q

echo
echo "=========================================================="
echo " .NET  —  LockFreeSkipListMap"
echo "=========================================================="
dotnet bench/LockFreeSkipList.Bench/bin/Release/net10.0/LockFreeSkipList.Bench.dll $QUICK --csv | tee /tmp/dotnet_bench.csv

echo
echo "=========================================================="
echo " Compiling + running Java benchmark (G1 GC)…"
echo "=========================================================="
( cd java && javac SkipListBench.java && \
  java -Xms2g -Xmx6g -XX:+UseG1GC SkipListBench $QUICK --csv ) | tee /tmp/java_bench.csv

echo
echo "=========================================================="
echo " Combined results"
echo "=========================================================="
{
  grep -h -v '^#' /tmp/dotnet_bench.csv
  grep -h -v '^#\|^variant' /tmp/java_bench.csv
} | column -t -s,
