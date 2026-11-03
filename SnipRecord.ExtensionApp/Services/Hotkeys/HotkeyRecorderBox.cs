using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace SnipRecord.ExtensionApp.Services.Hotkeys;

/// <summary>
/// Read-only TextBox that records a key combination. Backspace/Delete clears, Esc reverts to the value it had
/// when focused. Global hotkeys are suspended while it has focus so the combination reaches it.
/// </summary>
public sealed class HotkeyRecorderBox : TextBox
{
    private Hotkey _hotkey;
    private Hotkey _original;
    private bool _suspended;

    public HotkeyRecorderBox()
    {
        IsReadOnly = true;
        IsSpellCheckEnabled = false;
        PlaceholderText = "按下快捷键";
        Text = _hotkey.ToDisplayString();
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        PreviewKeyUp += OnPreviewKeyUp;
        GotFocus += (_, _) => { _original = _hotkey; SetSuspended(true); };
        LostFocus += (_, _) => { SetSuspended(false); Text = _hotkey.ToDisplayString(); };
        Unloaded += (_, _) => SetSuspended(false);
    }

    /// <summary>Raised when the user records or clears a combination (not when <see cref="Hotkey"/> is set in code).</summary>
    public event EventHandler<Hotkey>? HotkeyChanged;

    public Hotkey Hotkey
    {
        get => _hotkey;
        set { _hotkey = value; _original = value; Text = value.ToDisplayString(); }
    }

    private void SetSuspended(bool on)
    {
        if (_suspended == on) return;
        _suspended = on;
        if (on) GlobalHotkeyService.Shared.Suspend(); else GlobalHotkeyService.Shared.Resume();
    }

    private static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (Down(VirtualKey.Control)) m |= HotkeyModifiers.Ctrl;
        if (Down(VirtualKey.Menu)) m |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) m |= HotkeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) m |= HotkeyModifiers.Win;
        return m;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var vk = (int)e.Key;
        var mods = CurrentModifiers();
        if (Hotkey.IsModifierKey(vk))
        {
            var partial = new Hotkey(mods, 1).ToString(); // "Ctrl+Alt+VK01" → strip the fake key
            Text = partial[..partial.LastIndexOf('+')] + "+…";
            e.Handled = true;
            return;
        }
        if (mods == HotkeyModifiers.None)
        {
            switch (e.Key)
            {
                case VirtualKey.Tab: return; // keep focus navigation
                case VirtualKey.Escape: Commit(_original); e.Handled = true; return;
                case VirtualKey.Back or VirtualKey.Delete: Commit(Hotkey.None); e.Handled = true; return;
            }
        }
        e.Handled = true;
        var hk = new Hotkey(mods, vk);
        if (hk.IsValid) Commit(hk);
        else Text = "需包含 Ctrl、Alt 或 Win";
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (CurrentModifiers() == HotkeyModifiers.None && Hotkey.IsModifierKey((int)e.Key)) Text = _hotkey.ToDisplayString();
    }

    private void Commit(Hotkey hk)
    {
        var changed = hk != _hotkey;
        _hotkey = hk;
        Text = hk.ToDisplayString();
        if (changed) HotkeyChanged?.Invoke(this, hk);
    }
}
