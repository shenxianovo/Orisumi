#!/usr/bin/env python3
"""Install and start the current macOS collectors. Does not erase collected data."""
import argparse
import os
from pathlib import Path
import plistlib
import shutil
import signal
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--store', default='.orisumi')
    parser.add_argument('--extension-id', help='Register the installed Chrome extension; reload it in Chrome after rebuilding')
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('macOS graphical session required')
    root = Path(__file__).resolve().parents[1]
    dll = root / 'src/Orisumi.Cli/bin/Debug/net11.0/Orisumi.dll'
    dotnet = shutil.which('dotnet')
    if not dotnet or not dll.is_file():
        parser.error('Install .NET and run dotnet build Orisumi.slnx first')
    store = Path(args.store).resolve()
    logs = store / 'logs'
    logs.mkdir(parents=True, exist_ok=True)
    agents = Path.home() / 'Library/LaunchAgents'
    agents.mkdir(parents=True, exist_ok=True)
    base = [dotnet, str(dll), '--store', str(store)]
    if args.extension_id:
        subprocess.run([*base, 'browser', 'register-chrome', args.extension_id], check=True)
    domain = f'gui/{os.getuid()}'
    # Launchd is a different macOS permission context. Window/input capture uses
    # the already-authorized caller's context, and remains running after this command exits.
    foreground_label = 'dev.orisumi.capture.foreground'
    subprocess.run(['launchctl', 'bootout', f'{domain}/{foreground_label}'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    (agents / f'{foreground_label}.plist').unlink(missing_ok=True)
    pid_file = store / 'foreground.pid'
    if pid_file.exists():
        pid = int(pid_file.read_text())
        previous = subprocess.run(['ps', '-p', str(pid), '-o', 'command='], capture_output=True, text=True).stdout
        if str(dll) in previous and str(store) in previous and ' foreground ' in previous:
            os.kill(pid, signal.SIGTERM)
            for _ in range(100):
                try: os.kill(pid, 0)
                except ProcessLookupError: break
                time.sleep(0.05)
            else:
                raise RuntimeError('Previous foreground collector has not stopped')
    with (logs / 'foreground.out.log').open('ab') as stdout, (logs / 'foreground.err.log').open('ab') as stderr:
        foreground = subprocess.Popen([*base, 'foreground', '--windows', '--input'], stdin=subprocess.DEVNULL,
            stdout=stdout, stderr=stderr, start_new_session=True)
    time.sleep(0.5)
    if foreground.poll() is not None:
        raise RuntimeError(f"Foreground capture failed; see {logs / 'foreground.err.log'}")
    pid_file.write_text(str(foreground.pid))
    print(f'Started foreground collector: pid {foreground.pid}', flush=True)
    for name, command in [
        ('codex', ['codex', 'capture', '--all']),
        ('claude-code', ['claude', 'capture']),
    ]:
        label = f'dev.orisumi.capture.{name}'
        # Boot out the previous version before replacing its launch configuration.
        subprocess.run(['launchctl', 'bootout', f'{domain}/{label}'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        config = dict(Label=label, ProgramArguments=[*base, *command], WorkingDirectory=str(root),
            RunAtLoad=True, StandardOutPath=str(logs / f'{name}.out.log'),
            StandardErrorPath=str(logs / f'{name}.err.log'))
        config['StartInterval'] = 60
        path = agents / f'{label}.plist'
        path.write_bytes(plistlib.dumps(config))
        path.chmod(0o600)
        subprocess.run(['launchctl', 'enable', f'{domain}/{label}'], check=True)
        subprocess.run(['launchctl', 'bootstrap', domain, str(path)], check=True)
        print(f'Started {label}: every 60 seconds', flush=True)


if __name__ == '__main__':
    main()
