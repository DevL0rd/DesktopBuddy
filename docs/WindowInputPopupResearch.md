# Window Input Popup/Menu Research

## Problem

DesktopBuddy currently gates injected input on a foreground/root-owner check in `DesktopBuddy/Input/WindowInput.cs`:

- `IsFocusedForInput(hwnd)` compares `GetForegroundWindow()` against the target hwnd/root owner.
- `SendWhenFocused(...)` queues input if that check fails.
- `RequestFocus(...)` calls `BringWindowToTop`, `SetWindowPos`, `SetActiveWindow`, `SetFocus`, and `SetForegroundWindow` on the captured root window.

That works for normal unfocused windows, but it is the wrong shape for WGC secondary windows such as context menus, dropdowns, tooltips, and owned popups. If a context menu is already visible inside the captured texture, focusing the parent window before sending input can dismiss or bypass the actual popup window, so clicks land on the parent instead of the transient UI.

## Can We Change The Check To "Window Is Visible"?

Windows has `IsWindowVisible(hwnd)`, but it is not enough for this problem.

Microsoft documents that `IsWindowVisible` only checks whether the window and its ancestors have the `WS_VISIBLE` style. It may still return true even when the window is totally obscured by other windows. That means using it as the input gate could send input to a target that is technically visible but not actually the thing under the pointer.

So the answer is: yes, the API exists, but no, replacing "focused" with "visible" is not the clean fix.

## Better Option: Point-Based Hit Testing

The strongest path is to decide input safety from the screen point we are about to inject into.

Use the existing UV-to-screen conversion, then call `WindowFromPoint(screenPoint)`. Microsoft documents `WindowFromPoint` as returning the window containing a point, and it skips hidden/disabled windows. This is much closer to what we need than a broad visible/focused test.

Proposed decision flow:

1. Convert DesktopBuddy press/scroll/touch coordinates to screen coordinates.
2. Call `WindowFromPoint(screenPoint)`.
3. Classify the returned HWND:
   - It is the captured hwnd.
   - Its `GA_ROOTOWNER` matches the captured hwnd's root owner.
   - It is an owned popup of the captured root via `GetWindow(GW_OWNER)` chain.
   - It belongs to the same process/thread as the captured hwnd.
   - Optionally, it is a known transient popup/menu class discovered from logs, but this should be diagnostic, not the main rule.
4. If the point is already over an accepted target/secondary HWND, inject immediately without calling `RequestFocus` on the parent.
5. If the point is not over the target family and the target root is not foreground, use the existing focus queue path.

This makes the parent focus logic a fallback, not a mandatory step before every click.

## Menu-Specific Signal

`GetGUIThreadInfo(threadId, ...)` can tell us whether a GUI thread is in menu mode or popup menu mode. The returned `GUITHREADINFO` includes:

- `flags`, including `GUI_INMENUMODE` and `GUI_POPUPMENUMODE`.
- `hwndMenuOwner`.
- `hwndActive`, `hwndFocus`, and `hwndCapture`.

This is useful as a second confirmation when the target app has an active context menu:

- If the target thread reports `GUI_POPUPMENUMODE`, and `hwndMenuOwner` belongs to the captured window/root, do not focus the parent first.
- Let screen-coordinate injection go to the actual window under the cursor.

This also explains why `SetForegroundWindow` is fragile here: Microsoft documents that foreground changes are restricted, and one listed condition is that no menus are active. So forcing foreground while a menu is active is exactly the moment where Windows may refuse, behave oddly, or collapse the popup interaction.

## WinEvent Hook Support

We already have a foreground WinEvent hook. A cleaner future version could add process/thread-scoped hooks for:

- `EVENT_OBJECT_CREATE`
- `EVENT_OBJECT_SHOW`
- `EVENT_OBJECT_HIDE`
- `EVENT_OBJECT_DESTROY`
- `EVENT_OBJECT_LOCATIONCHANGE`
- `EVENT_OBJECT_FOCUS`

Microsoft documents that `SetWinEventHook` can filter by process/thread and that out-of-context hooks are queued in sequence on the hook thread. That would let us maintain a small live cache of transient popup/menu HWNDs for the captured app.

This should be supporting evidence and logging, not the sole routing mechanism. Popups are short-lived; the authoritative check at click time should still be `WindowFromPoint(screenPoint)`.

## Alternatives Considered

### Replace Focus Check With IsWindowVisible

Not recommended. It does not prove the target is actually under the pointer, and it does not identify popup/menu HWNDs. It would reduce some false negatives but adds a real risk of injecting into stale/covered targets.

### Spawn Separate DesktopBuddy Instances For Every Popup

Not recommended for context menus/tooltips. These windows are tiny, short-lived, often untitled, and already included in WGC secondary-window capture. Spawning full DesktopBuddy panels for them would be noisy and would race with their lifetime.

### Post Messages Directly To The Popup HWND

Risky. `PostMessage`/`SendMessage` input emulation is less hardware-like than `InjectTouchInput`/mouse input, and modern apps can ignore or mishandle synthetic window messages. It also would not preserve the current touch path cleanly.

### Use WindowFromPoint Plus Direct HWND Targeting For Touch

Worth experimenting with, but not as the first dependency. `POINTER_INFO.hwndTarget` exists and Microsoft describes it as the target/capture window for pointer messages, but `InjectTouchInput` primarily injects contacts by desktop screen coordinates. The first fix should avoid parent refocus and let Windows route by screen point.

## Recommended Plan

Implement a new point-aware input gate:

```text
SendAtPointWhenTargetAcceptable(hwnd, screenPoint, sendInput, description)
```

Behavior:

1. If `hwnd == IntPtr.Zero`, send immediately for monitor/desktop capture.
2. Resolve `hitHwnd = WindowFromPoint(screenPoint)`.
3. If `hitHwnd` belongs to the captured window family, send immediately.
4. If the captured app/thread is in popup menu mode and the menu owner belongs to the captured window family, send immediately.
5. Otherwise, fall back to the existing focus queue.

Important detail: compute the screen point before deciding whether to focus. Right now the focus decision only knows the captured hwnd, so it cannot distinguish "clicking the parent window" from "clicking a visible secondary popup that WGC baked into the texture."

## Sources

- Microsoft Learn: `IsWindowVisible` only checks `WS_VISIBLE` and can be true for totally obscured windows: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindowvisible
- Microsoft Learn: `WindowFromPoint` returns the window containing a screen point and skips hidden/disabled windows: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint
- Microsoft Learn: `GetAncestor(GA_ROOTOWNER)` walks parent and owner chains: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getancestor
- Microsoft Learn: `GetGUIThreadInfo` retrieves active/focus/menu-owner information for a GUI thread: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo
- Microsoft Learn: `GUITHREADINFO` exposes `GUI_INMENUMODE`, `GUI_POPUPMENUMODE`, and `hwndMenuOwner`: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-guithreadinfo
- Microsoft Learn: `SetForegroundWindow` is restricted and requires no active menus among its conditions: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
- Microsoft Learn: `SetWinEventHook` can filter by process/thread and requires a message loop: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
- Microsoft Learn: WinEvent constants include object create/destroy/focus/location and menu-related events: https://learn.microsoft.com/en-us/windows/win32/winauto/event-constants
- Microsoft Learn: `InjectTouchInput` injects contacts by desktop screen coordinates: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injecttouchinput
