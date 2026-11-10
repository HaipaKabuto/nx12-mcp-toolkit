"""Run the NX12 MCP service using one local install configuration.

由本人修改（仓库维护者 @HaipaKabuto）, 2026-11-10.
GPL-3.0-only; see the repository LICENSE and docs/ATTRIBUTION.md.
"""
import argparse
import json
import os
from pathlib import Path
import runpy
import sys


def main():
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=root / 'data' / 'install.json')
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    if config.get('schemaVersion') != 1:
        raise ValueError('Unsupported local installation schema')
    source = root / 'mcp' / 'src'
    if not (source / 'nx_mcp' / 'server.py').is_file():
        raise FileNotFoundError('NX12 MCP sources are missing')
    sys.path.insert(0, str(source))
    sys.dont_write_bytecode = True
    nx_home = Path(config['nxHome'])
    os.environ.update(
        NX_HOME=str(nx_home), UGII_BASE_DIR=str(nx_home),
        UGII_ROOT_DIR=str(nx_home / 'UGII') + os.sep,
        UGII_USER_DIR=config['nxUserDirectory'], NX_MCP_BACKEND='loader',
        NX_MCP_WORKSPACE=config['workspace'], NX_MCP_ENABLE_AUTOMATION='1',
        NX_MCP_ENABLE_EXPERIMENTAL='0', NX_MCP_ENABLE_JOURNAL='0',
        PYTHONDONTWRITEBYTECODE='1', PYTHONNOUSERSITE='1',
    )
    for name in ('QT_PLUGIN_PATH', 'QT_QPA_PLATFORM_PLUGIN_PATH', 'QML2_IMPORT_PATH',
                 'QML_IMPORT_PATH', 'UGII_QT_PLUGIN_PATH', 'NX_MCP_AUDIT_ROOT'):
        os.environ.pop(name, None)
    runpy.run_module('nx_mcp.server', run_name='__main__')


if __name__ == '__main__':
    main()
