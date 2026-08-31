#!/usr/bin/env bash
#
# Run the SWP Robot suites against a Renode checkout.
#
#   RENODE_ROOT=/path/to/renode ./run-tests.sh
#
# Renode's own renode-test harness is used, so its Python requirements must be
# installed (tests/requirements.txt in the Renode tree).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RENODE_ROOT="${RENODE_ROOT:-$HOME/renode}"

if [ ! -x "$RENODE_ROOT/renode-test" ]; then
    echo "renode-test not found under '$RENODE_ROOT'." >&2
    echo "Clone and build Renode, then set RENODE_ROOT to it:" >&2
    echo "    git clone --recurse-submodules https://github.com/renode/renode.git" >&2
    echo "    cd renode && ./build.sh" >&2
    exit 1
fi

# The firmware suite loads a prebuilt ELF; build it if it is not there yet.
if [ ! -f "$HERE/firmware/build/swp-demo.elf" ]; then
    echo "Building the demo firmware..."
    make -C "$HERE/firmware"
fi

exec "$RENODE_ROOT/renode-test" \
    "$HERE/tests/swp.robot" \
    "$HERE/tests/nucleo_h533re_swp.robot" \
    "$@"
