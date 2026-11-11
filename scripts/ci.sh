#!/bin/sh
set -eu
# Run the test suite from the repo root.
cd "$(dirname "$0")/.."
dotnet test
