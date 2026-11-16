#!/usr/bin/env python3
"""Compatibility entry point for the maintained landing validator (requires Node)."""
from pathlib import Path
import subprocess
import sys
sys.exit(subprocess.call(["node", str(Path(__file__).with_suffix(".cjs"))]))
