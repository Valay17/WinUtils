namespace ScreenDimmer;

internal sealed class PopupMenu : IDisposable
{
    // Ids start above zero: TrackPopupMenu returns 0 for "dismissed without
    // choosing", which would otherwise be indistinguishable from the first item.
    private const int FirstId = 100;

    private const string PlaceholderText = "Adjust Value using Slider";

    private readonly IntPtr handle_;
    private readonly List<IntPtr> submenus_ = new();
    private readonly Dictionary<int, Action> actions_ = new();

    private readonly Dictionary<IntPtr, Action<Win32.RECT>> customTriggers_ = new();

    private readonly IntPtr backgroundBrush_;

    private int nextId_ = FirstId;
    private bool disposed_;

    internal PopupMenu()
    {
        handle_ = Win32.CreatePopupMenu();
        if (handle_ == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed.");
        }

        backgroundBrush_ = Win32.CreateSolidBrush(Win32.Rgb(0x2b, 0x2b, 0x2b));
        var info = new Win32.MENUINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MENUINFO>(),
            fMask = Win32.MIM_BACKGROUND,
            hbrBack = backgroundBrush_,
        };
        Win32.SetMenuInfo(handle_, in info);
    }

    private PopupMenu(IntPtr handle, PopupMenu root)
    {
        handle_ = handle;
        actions_ = root.actions_;
        Root = root;
    }

    private PopupMenu? Root { get; }

    internal void Add(string text, Action? action = null, bool enabled = true, bool @checked = false)
    {
        var flags = Win32.MF_STRING;

        if (!enabled || action is null)
        {
            flags |= Win32.MF_GRAYED;
        }

        if (@checked)
        {
            flags |= Win32.MF_CHECKED;
        }

        var id = AllocateId();
        if (action is not null)
        {
            (Root ?? this).actions_[id] = action;
        }

        Win32.AppendMenuW(handle_, flags, new IntPtr(id), text);
    }

    internal void AddSeparator() =>
        Win32.AppendMenuW(handle_, Win32.MF_SEPARATOR, IntPtr.Zero, null);

    internal PopupMenu AddSubmenu(string text)
    {
        var child = Win32.CreatePopupMenu();
        if (child == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed for a submenu.");
        }

        Win32.AppendMenuW(handle_, Win32.MF_STRING | Win32.MF_POPUP, child, text);
        submenus_.Add(child);

        return new PopupMenu(child, Root ?? this);
    }

    internal void AddCustomTrigger(string text, Action<Win32.RECT> onOpen)
    {
        var child = Win32.CreatePopupMenu();
        if (child == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed for a custom trigger.");
        }

        Win32.AppendMenuW(child, Win32.MF_STRING | Win32.MF_GRAYED, IntPtr.Zero, PlaceholderText);
        Win32.AppendMenuW(handle_, Win32.MF_STRING | Win32.MF_POPUP, child, text);
        submenus_.Add(child);
        (Root ?? this).customTriggers_[child] = onOpen;
    }

    internal bool TryGetCustomTrigger(IntPtr childMenu, out Action<Win32.RECT>? onOpen) =>
        (Root ?? this).customTriggers_.TryGetValue(childMenu, out onOpen);

    private int AllocateId()
    {
        var root = Root ?? this;
        return root.nextId_++;
    }

    internal void ShowAndDispatch(IntPtr owner, int x, int y)
    {
        TrayInterop.ForceForeground(owner);

        var chosen = Win32.TrackPopupMenuEx(
            handle_,
            Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN | Win32.TPM_LEFTALIGN,
            x, y, owner, IntPtr.Zero);

        TrayInterop.NudgeMessageQueue(owner);

        if (chosen != 0 && (Root ?? this).actions_.TryGetValue(chosen, out var action))
        {
            action();
        }
    }

    public void Dispose()
    {
        if (disposed_ || Root is not null)
        {
            return;
        }

        disposed_ = true;
        submenus_.Clear();
        Win32.DestroyMenu(handle_);

        if (backgroundBrush_ != IntPtr.Zero)
        {
            Win32.DeleteObject(backgroundBrush_);
        }
    }
}
