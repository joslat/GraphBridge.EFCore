"""Inspect a generated Query Lab artifact in a fresh, owned headless Chrome process."""
import argparse
import base64
import ctypes
import hashlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from urllib.request import urlopen
from websockets.sync.client import connect

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('report', type=Path)
parser.add_argument('--kind', choices=['offline', 'inspect', 'semantic', 'contrast', 'adoption'], required=True)
parser.add_argument('--chrome', type=Path, default=os.environ.get('GRAPHBRIDGE_CHROME_EXECUTABLE'), help='Installed Chrome executable (Windows acceptance harness).')
args = parser.parse_args()
if sys.platform != 'win32': raise SystemExit('Static capture harness currently qualifies Windows only.')
chrome = args.chrome or Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Google/Chrome/Application/chrome.exe'
if not chrome.is_file(): raise SystemExit('Supply --chrome or GRAPHBRIDGE_CHROME_EXECUTABLE.')
report = args.report.resolve()
assert report.is_relative_to(root) and report.is_file()
out = root / '.artifacts/query-lab' / args.kind
out.mkdir(parents=True, exist_ok=True)

class Memory(ctypes.Structure):
    _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [(key, ctypes.c_ulonglong) for key in ['total', 'available', 'page', 'freepage', 'virtual', 'freevirtual', 'extended']]

def memory():
    result = Memory()
    result.length = ctypes.sizeof(result)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(result)): raise RuntimeError('Cannot measure available RAM')
    return result.available / 2**30

assert memory() > 6, 'Insufficient browser headroom above shared reserve'
profile = out / ('browser-' + str(time.time_ns()))
proc = subprocess.Popen([str(chrome), '--headless=new', '--no-first-run', '--disable-background-networking', '--disable-extensions', '--disable-component-update', '--remote-debugging-port=0', '--remote-allow-origins=http://localhost', '--user-data-dir=' + str(profile), 'about:blank'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
ws = None
serial = 0
errors, requests = [], []
try:
    for _ in range(100):
        port_file = profile / 'DevToolsActivePort'
        if port_file.exists(): break
        if proc.poll() is not None: raise RuntimeError('Owned browser exited during startup')
        time.sleep(.1)
    port = port_file.read_text().splitlines()[0]
    pages = json.load(urlopen('http://127.0.0.1:' + port + '/json', timeout=5))
    ws = connect(next(page['webSocketDebuggerUrl'] for page in pages if page['type'] == 'page'), origin='http://localhost', open_timeout=5)

    def call(method, params=None):
        global serial
        serial += 1
        current = serial
        ws.send(json.dumps({'id': current, 'method': method, 'params': params or {}}))
        while True:
            message = json.loads(ws.recv(timeout=20))
            if message.get('method') == 'Runtime.exceptionThrown': errors.append(message)
            if message.get('method') == 'Network.requestWillBeSent': requests.append(message['params']['request']['url'])
            if message.get('id') == current:
                if 'error' in message: raise RuntimeError(message['error'])
                return message.get('result', {})

    def js(expression):
        result = call('Runtime.evaluate', {'expression': expression, 'returnByValue': True})
        if 'exceptionDetails' in result: raise RuntimeError(result['exceptionDetails'])
        return result.get('result', {}).get('value')

    def screenshot(name):
        (out / name).write_bytes(base64.b64decode(call('Page.captureScreenshot', {'format': 'png', 'captureBeyondViewport': False})['data']))

    def scroll(selector):
        js("document.querySelector(" + json.dumps(selector) + ").scrollIntoView({behavior:'instant',block:'start'})")
        assert abs(js("document.querySelector(" + json.dumps(selector) + ").getBoundingClientRect().top")) <= 40 or js("Math.abs(document.documentElement.scrollHeight-scrollY-innerHeight)<=2")

    call('Page.enable'); call('Runtime.enable'); call('Network.enable')
    call('Emulation.setScriptExecutionDisabled', {'value': True})
    call('Emulation.setDeviceMetricsOverride', {'width': 1440, 'height': 1100, 'deviceScaleFactor': 1, 'mobile': False})
    call('Emulation.setEmulatedMedia', {'features': [{'name': 'prefers-reduced-motion', 'value': 'reduce'}]})
    call('Page.navigate', {'url': report.as_uri()})
    for _ in range(100):
        if js('document.readyState') == 'complete': break
        time.sleep(.1)
    if args.kind == 'adoption':
        assert js("document.querySelectorAll('article').length") == 3
        assert js("document.querySelectorAll('script,iframe,img,link,object,embed,form').length") == 0
        assert js('document.documentElement.scrollWidth <= innerWidth')
        for href in js("[...document.querySelectorAll('a')].map(a=>a.getAttribute('href'))"):
            assert (report.parent / href).resolve().is_file(), href
        screenshot('overview.png')
        for section in ('code-first', 'fluent', 'authoring'):
            scroll('#' + section)
            screenshot(section + '.png')
        call('Emulation.setDeviceMetricsOverride', {'width': 390, 'height': 844, 'deviceScaleFactor': 1, 'mobile': True})
        js("scrollTo({top:0,behavior:'instant'})")
        assert js('document.documentElement.scrollWidth <= innerWidth')
        screenshot('mobile.png')
        assert not errors and not [url for url in requests if url.startswith(('http:', 'https:', 'ws:', 'wss:'))]
        assert memory() > 5
        result = {'kind': 'adoption', 'cards': 3, 'screenshots': 5, 'scripts_disabled': True,
                  'all_links_exist': True, 'desktop_mobile_bounds': True, 'network_requests': 0,
                  'report_sha256': hashlib.sha256(report.read_bytes()).hexdigest()}
        (out / 'verification.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result))
        sys.exit(0)
    queries, patterns, sources = (1, 2, 0) if args.kind == 'contrast' else (3, 4, 3)
    assert js("document.querySelectorAll('article.query').length") == queries
    assert js("document.querySelectorAll('svg.pattern').length") == patterns
    assert js("document.querySelectorAll('.source-code').length") == sources
    assert js("document.querySelectorAll('script,iframe,img,link,object,embed,form').length") == 0
    assert js("[...document.querySelectorAll('[id]')].map(e=>e.id).length===new Set([...document.querySelectorAll('[id]')].map(e=>e.id)).size")
    assert js("[...document.querySelectorAll('a[href^=\"#\"]')].every(a=>document.getElementById(a.hash.slice(1)))")
    assert js('document.documentElement.scrollWidth <= innerWidth'), 'Desktop page overflow'
    assert js("document.querySelectorAll('.plan').length") == (0 if args.kind == 'offline' else queries)
    if args.kind == 'semantic':
        assert js("[...document.querySelectorAll('.query .stages')].every(e=>e.querySelectorAll('.badge.pass').length===3)")
        assert js("document.body.textContent.includes('Fluent vs native')")
    elif args.kind == 'contrast':
        assert js("document.querySelector('.hero').textContent.includes('Fail')")
        assert js("document.body.textContent.includes('8 / 6')")
        assert js("document.body.textContent.includes('WHERE belongs to this With clause')")
        assert not js("document.body.textContent.includes('WHERE belongs to this OptionalMatch clause')")
    else:
        assert js("document.body.textContent.includes('Semantic comparison not run')")
    screenshot('overview.png')
    scroll('#query-0')
    screenshot('coactors.png')
    scroll('#query-0 .clause')
    screenshot('coactors-pattern.png')
    scroll('#query-0 .optional' if args.kind == 'contrast' else '#query-1 .optional')
    screenshot('optional.png')
    scroll('#query-0 .panel:has(.plan) table' if args.kind == 'contrast' else '#query-2 .aggregates')
    screenshot('comparison.png' if args.kind == 'contrast' else 'shared-counts.png')
    call('Emulation.setDeviceMetricsOverride', {'width': 390, 'height': 844, 'deviceScaleFactor': 1, 'mobile': True})
    js("scrollTo({top:0,behavior:'instant'})")
    assert js('document.documentElement.scrollWidth <= innerWidth'), 'Mobile page overflow'
    screenshot('mobile.png')
    scroll('#query-0 .clause')
    assert js('document.documentElement.scrollWidth <= innerWidth'), 'Mobile graph overflow'
    screenshot('mobile-pattern.png')
    call('Emulation.setDeviceMetricsOverride', {'width': 1200, 'height': 1000, 'deviceScaleFactor': 1, 'mobile': False})
    call('Emulation.setEmulatedMedia', {'media': 'print'})
    assert js("[...document.querySelectorAll('pre')].every(p=>getComputedStyle(p).maxHeight==='none')")
    assert js("[...document.querySelectorAll('.pattern-data')].every(d=>d.open)")
    pdf = base64.b64decode(call('Page.printToPDF', {'printBackground': True, 'preferCSSPageSize': True})['data'])
    assert pdf.startswith(b'%PDF-') and len(pdf) > 10_000
    (out / 'report.pdf').write_bytes(pdf)
    assert not errors, errors
    assert not [url for url in requests if url.startswith(('http:', 'https:', 'ws:', 'wss:'))], requests
    assert memory() > 5, 'Shared RAM reserve breached'
    result = {'kind': args.kind, 'queries': queries, 'patterns': patterns, 'sources': sources, 'checks': ['scripts disabled', 'no active/external content', 'all clause links resolve', 'unique IDs', 'desktop/mobile layout bounds', 'truthful tier observations', 'print unclipped source and pattern tables', 'PDF generated', 'no network requests or JavaScript errors'], 'screenshots': 7, 'pdf_bytes': len(pdf), 'available_ram_gib': round(memory(), 2)}
    result['report_sha256'] = hashlib.sha256(report.read_bytes()).hexdigest()
    (out / 'verification.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))
finally:
    if ws:
        try: call('Browser.close')
        except Exception: pass
        ws.close()
    try: proc.wait(timeout=10)
    except subprocess.TimeoutExpired: proc.terminate(); proc.wait(timeout=10)
