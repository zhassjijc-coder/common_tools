[简体中文](system-settings.md) | **English**

# Device Debug Tools · System settings

The former Quick tools module is now **系统配置** (System settings). Capture, image pinning and offline OCR remain on this page.

| Option | Behavior |
| --- | --- |
| 开机自启动（后台运行） | Start after the current Windows user signs in; show only the tray icon |
| 启动时默认启用截图功能 | Register F1 capture / F3 pinning on the next manual or automatic launch |
| 当前启用截图快捷键 | Enable or disable capture now, independently of the next-start preference |
| 隐藏到托盘 | Hide the main window while keeping communication and enabled capture running |

The first two options are initially off and changes are saved immediately. Launch-at-sign-in alone does not enable capture. Select both options for background capture. A hotkey conflict leaves capture disabled, reports the error and preserves the next-start preference.

Double-click the tray icon to restore the window. Its menu opens system settings, toggles capture or exits. Launching another copy activates the resident new-version instance in the same Windows session. The main window X still exits and releases connections, hotkeys and floating windows.

Automatic launch uses the current user's Windows Run entry without administrator privileges. Keep the complete portable folder in a stable location. Exit the old version before running an upgrade; click **更新为当前版本** (Update to current version) if the startup path differs. Update it after moving the folder too. If the entry is registered but not running, check that Windows Startup apps allows Device Debug Tools (the executable remains `LinkLab.exe`).

Windows may delay startup applications. This feature runs after user sign-in and is not a pre-login Windows service. [Microsoft Run entry documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

The capture preference is saved in `%LOCALAPPDATA%\LinkLab\settings.json`; startup registration is read from Windows. Loading preferences does not recreate a removed startup entry.

## Capture editing

Enable capture, press **F1**, and drag to select a region. You can then continue editing:

- Use the move tool to drag the selection; drag its edges or eight handles to resize it.
- Zoom the canvas with the mouse wheel or toolbar controls. Hold Space while dragging, or drag with the middle mouse button, to pan. Preview zoom does not change the original resolution of exported images.
- Add text, rectangle or square annotations inside the selection. Change annotation colors with the toolbar palette and use Undo to step back through annotations.
- Copy, save and pin export the annotated image. OCR reads the original region so annotations do not interfere with recognition.
- Press **Enter** to complete capture or **Esc** to cancel.

After **F3** pinning, drag the floating image to move it and use the wheel to zoom. Reset its scale to return to 100%; **Ctrl + wheel** changes opacity. The pin menu also offers copying and OCR.
