import io
import sys

def patch(path, subs):
    s = io.open(path, encoding='utf-8').read()
    for old, new in subs:
        if s.count(old) != 1:
            print('MISS in %s (%d): %s' % (path, s.count(old), old[:70]))
            sys.exit(1)
        s = s.replace(old, new)
    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print('OK ' + path)

patch('OcrTool.App/Program.cs', [
    ('Logger.Log("WARNING: Low-level keyboard hook failed to install");',
     'Logger.Log("WARNING: RegisterHotKey failed");'),
    ('BalloonTipText = "Hotkey hook failed to install.\\nTry restarting the application.";',
     'BalloonTipText = "Hotkey registration failed.\\nTry restarting the application.";'),
    ('Logger.Log("Hotkey hook installed successfully");',
     'Logger.Log("Hotkey registered successfully");'),
    ('Logger.Log($"New hotkey hook installed for: {_config.Hotkey}");',
     'Logger.Log($"New hotkey registered for: {_config.Hotkey}");'),
])

patch('OcrTool.App/OcrTool.App.csproj', [
    ('<Version>1.0.0</Version>', '<Version>1.0.1</Version>'),
])

changelog_section = '''# Changelog

## 1.0.1

### Background power
- The global hotkey now uses `RegisterHotKey` instead of a low-level
  keyboard hook (`WH_KEYBOARD_LL`): the process is woken only for the exact
  hotkey combination instead of for every keystroke made system-wide.
  Idle CPU in the tray remains 0.
- Removed a duplicated `GCHandle.Alloc` in the hotkey constructor.
'''
patch('CHANGELOG.md', [('# Changelog\n', changelog_section)])
print('PATCH_ALL_OK')
