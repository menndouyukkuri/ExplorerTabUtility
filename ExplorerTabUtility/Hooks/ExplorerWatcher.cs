﻿using Shell32;
using SHDocVw;
using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Hooks;

using WindowEntry = DualKeyEntry<InternetExplorer, nint?, WindowInfo>;

public class ExplorerWatcher : IHook
{
    private static bool _created;
    private static Guid _shellBrowserGuid = typeof(IShellBrowser).GUID;

    private ShellWindows _shellWindows = null!;
    private ShellPathComparer _shellPathComparer = null!;
    private StaTaskScheduler _staTaskScheduler = null!;
    private CancellationTokenSource? _processMonitorCts;

    private nint _mainWindowHandle;
    private long _lastClosedTimestamp;
    private readonly DualKeyDictionary<InternetExplorer, nint?, WindowInfo> _windowEntryDict = [];
    private readonly ConcurrentStack<WindowRecord> _closedWindows = new();
    private readonly object _windowEntryDictLock = new(), _closedWindowsLock = new();
    private readonly SemaphoreSlim _toOpenWindowsLock = new(1);

    private nint _eventObjectShowHookId;
    private WinEventDelegate? _eventObjectShowHookCallback;
    private DShellWindowsEvents_WindowRegisteredEventHandler? _windowRegisteredHandler;

    private bool _reuseTabs = true;
    private bool _isForcingTabs;
    public bool IsHookActive => _isForcingTabs;

    public ExplorerWatcher()
    {
        if (_created)
            throw new InvalidOperationException("Only one instance of ExplorerWatcher is allowed at a time.");
        _created = true;

        InitializeShellObjects();
    }

    public void StartHook()
    {
        if (_isForcingTabs) return;
        _isForcingTabs = true;
    }

    public void StopHook()
    {
        if (!_isForcingTabs) return;
        _isForcingTabs = false;
    }
    public void SetReuseTabs(bool reuseTabs) => _reuseTabs = reuseTabs;

    public nint SearchForTab(string targetPath)
    {
        nint targetPidl = 0;
        try
        {
            targetPidl = _shellPathComparer.GetPidlFromPath(targetPath);
            if (targetPidl == 0) return 0;

            foreach (var (window, windowInfo, tabHandle) in _windowEntryDict)
            {
                // Make sure it is not the newly created window
                if (!Helper.IsTimeUp(windowInfo.CreatedAt, 2_000) || !tabHandle.HasValue || tabHandle.Value == 0)
                    continue;

                var comparePath = GetLocation(window);

                if (_shellPathComparer.IsEquivalent(targetPath, comparePath, targetPidl))
                    return tabHandle.Value;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (targetPidl != 0)
                Marshal.FreeCoTaskMem(targetPidl);
        }
    }
    public async Task SelectTabByHandle(nint windowHandle, nint tabHandle)
    {
        var tabs = Helper.GetAllExplorerTabs(windowHandle).ToArray();
        if (tabs.Length == 0) return;

        var activeTab = tabs[0];
        for (var i = 0; i < tabs.Length; i++)
        {
            if (activeTab == tabHandle) break;

            SelectTabByIndex(windowHandle, i);

            // ReSharper disable once AccessToModifiedClosure
            activeTab = await Helper.DoUntilConditionAsync(
                () => WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null),
                h => h != activeTab).ConfigureAwait(false);
        }
    }
    public void SelectLastTab(nint windowHandle)
    {
        var count = Helper.GetAllExplorerTabs(windowHandle).Count();
        SelectTabByIndex(windowHandle, count - 1);
    }
    public void SelectTabByIndex(nint windowHandle, int index)
    {
        // Send 0xA221 magic command (CTRL + 1...n)
        WinApi.SendMessage(windowHandle, WinApi.WM_COMMAND, 0xA221, index + 1);
    }
    public void RequestToOpenNewTab(nint windowHandle, bool bringToFront = false)
    {
        if (bringToFront && windowHandle == 0)
            windowHandle = GetMainWindowHWnd(0);

        if (windowHandle == 0)
        {
            Process.Start("explorer.exe");
            return;
        }

        var tabHandle = WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
        if (tabHandle == 0) return;

        // Send 0xA21B magic command (CTRL + T)
        WinApi.PostMessage(tabHandle, WinApi.WM_COMMAND, 0xA21B, 0);

        if (bringToFront)
            WinApi.RestoreWindowToForeground(windowHandle);
    }
    public async Task Open(string? location, nint windowHandle, int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(location))
        {
            RequestToOpenNewTab(windowHandle, bringToFront: true);
            return;
        }

        OpenNewTab(windowHandle, NormalizeLocation(location!));
    }
    public void OpenNewTab(nint windowHandle, string location)
    {
        _ = OpenTabNavigateWithSelection(new WindowRecord(location, windowHandle), windowHandle);
    }
    public void DuplicateActiveTab(nint windowHandle)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        if (window == null) return;

        var location = GetLocation(window);
        var selectedItems = GetSelectedItems(window);
        var windowRecord = new WindowRecord(location, windowHandle, selectedItems);
        _ = OpenTabNavigateWithSelection(windowRecord, windowHandle, isDuplicate: true);
    }
    public void ReopenClosedTab(nint windowHandle = 0)
    {
        WindowRecord? closedWindow;
        lock (_closedWindowsLock)
            if (!_closedWindows.TryPop(out closedWindow)) return;

        _ = OpenTabNavigateWithSelection(closedWindow, windowHandle);
    }
    public void SetTargetWindow(nint windowHandle)
    {
        if (Helper.IsFileExplorerWindow(windowHandle))
            _mainWindowHandle = windowHandle;
    }
    public void NavigateBack(nint windowHandle)
    {
        var activeTabHandle = GetActiveTabHandle(windowHandle);
        if (activeTabHandle == 0) return;

        var window = GetWindowByTabHandle(activeTabHandle);
        try
        {
            window?.GoBack();
        }
        catch
        {
            // Will throw if there is no further history
        }
    }
    private void OnWindowShown(nint hWinEventHook, uint eventType, nint hWnd, int idObject, int idChild, uint dwEventThread, uint dWmsEventTime)
    {
        if (!_isForcingTabs || idObject != 0 || idChild != 0) return;
        if (!WinApi.IsWindowHasClassName(hWnd, "CabinetWClass")) return;

        if (_windowEntryDict.Count < 2) return;
        WinApi.SetWindowTransparency(hWnd, 0);
    }
    private InternetExplorer? GetRecentlyCreatedWindow(out WindowInfo? windowInfo)
    {
        // When a new window is registered, it's typically the last in the collection
        var count = _shellWindows.Count;
        for (var i = count - 1; i >= 0; i--)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window) continue;

            lock (_windowEntryDictLock)
            {
                if (_windowEntryDict.Keys.Contains(window)) continue;

                windowInfo = new WindowInfo();
                _windowEntryDict.Add(window, windowInfo);
                return window;
            }
        }

        windowInfo = null;
        return null;
    }
    private async void OnShellWindowRegistered(int __)
    {
        var showAgain = true;
        nint hWnd = 0;
        try
        {
            WindowInfo windowInfo = null!;
            var window = await Helper.DoUntilNotDefaultAsync(() => GetRecentlyCreatedWindow(out windowInfo!), 2_000, 40).ConfigureAwait(false);
            if (window == null) return;

            _ = GetTabHandle(window);

            hWnd = new IntPtr(window.HWND);
            var location = GetLocation(window);

            //Control Panel
            if (location.StartsWith("shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}"))
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // Check if this is a single tab window and there are other windows
            var shouldReopenAsTab = (_isForcingTabs || _reuseTabs) &&
                                    _windowEntryDict.Count > 1 &&
                                    Helper.GetAllExplorerTabs(hWnd).Take(2).Count() == 1;

            if (shouldReopenAsTab)
                WinApi.SetWindowTransparency(hWnd, 0);

            // Check if it is a detached tab
            var isDetachedReattached = IsDetachedReattached(location, out var closedWindow);
            if (isDetachedReattached)
                SelectItems(window, closedWindow!.SelectedItems);

            shouldReopenAsTab = shouldReopenAsTab && !isDetachedReattached;

            if (shouldReopenAsTab)
            {
                showAgain = false;

                _ = OpenTabNavigateWithSelection(new WindowRecord(location, hWnd, GetSelectedItems(window)));

                window.Quit();
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            // OnQuit might fire after ShellWindowRegistered in case of reattached tab (and there were selected files)
            if (!isDetachedReattached)
            {
                isDetachedReattached = await Helper.DoUntilNotDefaultAsync(() => IsDetachedReattached(location, out closedWindow), 700, 50).ConfigureAwait(false);
                if (isDetachedReattached)
                    SelectItems(window, closedWindow!.SelectedItems);
            }

            HookWindowEvents(window, windowInfo);
        }
        catch {/**/}
        finally
        {
            if (showAgain)
            {
                WinApi.SetWindowTransparency(hWnd, 255);

                // OnWindowShown might fire after ShellWindowRegistered and hide it again.
                await Task.Delay(1500).ConfigureAwait(false);
                WinApi.SetWindowTransparency(hWnd, 255);
            }
        }
    }
    private void HookWindowEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        // Create strongly-typed handlers so we can remove them later
        windowInfo.OnQuitHandler = () =>
        {
            var location = GetLocation(window);

            // Home, This PC
            if (location is "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}" or "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")
            {
                RemoveWindowAndUnhookEvents(window, windowInfo);
                return;
            }

            _lastClosedTimestamp = Stopwatch.GetTimestamp();
            var windowRecord = new WindowRecord(location, new IntPtr(window.HWND));
            lock (_closedWindowsLock)
                _closedWindows.Push(windowRecord);

            windowRecord.SelectedItems = GetSelectedItems(window);

            RemoveWindowAndUnhookEvents(window, windowInfo);
        };

        // Subscribe
        window.OnQuit += windowInfo.OnQuitHandler;
    }
    private void RemoveWindowAndUnhookEvents(InternetExplorer window, WindowInfo windowInfo)
    {
        // Unsubscribe
        if (windowInfo.OnQuitHandler != null)
            window.OnQuit -= windowInfo.OnQuitHandler;

        // Remove from dictionary
        _windowEntryDict.Remove(window);

        // Finally release the COM reference for this InternetExplorer instance
        Marshal.ReleaseComObject(window);
    }

    private async Task OpenTabNavigateWithSelection(WindowRecord toOpenWindow, nint windowHandle = 0, bool isDuplicate = false)
    {
        await _toOpenWindowsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_reuseTabs && !isDuplicate && _windowEntryDict.Count > 0)
            {
                var existingTab = SearchForTab(toOpenWindow.Location);
                if (existingTab != 0)
                {
                    windowHandle = WinApi.GetParent(existingTab);
                    await SelectTabByHandle(windowHandle, existingTab).ConfigureAwait(false);
                    WinApi.RestoreWindowToForeground(windowHandle);
                    return;
                }
            }

            // Get the main window
            var mainWindowHWnd = Helper.IsFileExplorerWindow(windowHandle)
                ? windowHandle
                : GetMainWindowHWnd(toOpenWindow.Handle);

            // Store the current tabs
            var currentTabs = Helper.GetAllExplorerTabs(mainWindowHWnd).ToArray();

            // Request to open a new tab
            RequestToOpenNewTab(mainWindowHWnd);

            // Wait for the new tab
            var newTabHandle = await Helper.ListenForNewExplorerTabAsync(mainWindowHWnd, currentTabs).ConfigureAwait(false);
            if (newTabHandle == 0) return;

            // Get the window object
            var window = await Helper.DoUntilNotDefaultAsync(() => GetWindowByTabHandle(newTabHandle), 2_000, 50).ConfigureAwait(false);
            if (window == null) return;

            var tcs = new TaskCompletionSource<bool>();
            DWebBrowserEvents2_NavigateComplete2EventHandler navigateHandler = null!;
            navigateHandler = (object _, ref object _) =>
            {
                window.NavigateComplete2 -= navigateHandler;
                tcs.TrySetResult(true);
                SelectItems(window, toOpenWindow.SelectedItems);
            };

            window.NavigateComplete2 += navigateHandler;
            window.Navigate2(toOpenWindow.Location);

            WinApi.RestoreWindowToForeground(mainWindowHWnd);

            var timeoutTask = Task.Delay(5000);
            await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
        }
        finally
        {
            _toOpenWindowsLock.Release();
        }
    }
    private bool IsDetachedReattached(string incomingLocation, out WindowRecord? closedWindow)
    {
        lock (_closedWindowsLock)
        {
            if (!Helper.IsTimeUp(_lastClosedTimestamp, 2_000) &&
                _closedWindows.TryPeek(out closedWindow) &&
                closedWindow.Location == incomingLocation)
            {
                _closedWindows.TryPop(out _);
                return true;
            }
        }

        closedWindow = null;
        return false;
    }
    private nint GetMainWindowHWnd(nint otherThan)
    {
        if (Helper.IsFileExplorerWindow(_mainWindowHandle))
            return _mainWindowHandle;

        var allWindows = WinApi.FindAllWindowsEx("CabinetWClass");

        // Get another handle other than the newly created one. (In case if it is still alive.)
        _mainWindowHandle = allWindows
            .Where(h => h != otherThan)
            .Reverse() // To get the last one in the z-index (the oldest)
            .OrderByDescending(h => WinApi.FindAllWindowsEx("ShellTabWindowClass", h).Count()) // The one with the most tabs first
            .FirstOrDefault();

        return _mainWindowHandle == 0 ? otherThan : _mainWindowHandle;
    }
    private Task<nint> GetTabHandle(InternetExplorer window)
    {
        if (_windowEntryDict.TryGetValue(window, out WindowEntry entry) && entry.OptionalKey is { } handle and > 0)
            return Task.FromResult(handle);

        // Schedule the operation on STA
        return Task.Factory.StartNew(() =>
        {
            if (window is not Interop.IServiceProvider sp) return 0;

            sp.QueryService(ref _shellBrowserGuid, ref _shellBrowserGuid, out var shellBrowser);
            if (shellBrowser == null) return 0;

            try
            {
                shellBrowser.GetWindow(out var hWnd);

                if (hWnd != 0)
                    _windowEntryDict.UpdateOptionalKey(window, hWnd);

                return hWnd;
            }
            finally
            {
                Marshal.ReleaseComObject(shellBrowser);
            }
        },
        CancellationToken.None,
        TaskCreationOptions.None,
        _staTaskScheduler);
    }
    private static nint GetActiveTabHandle(nint windowHandle)
    {
        // Active tab always at the top of the z-index
        return WinApi.FindWindowEx(windowHandle, 0, "ShellTabWindowClass", null);
    }
    private InternetExplorer? GetWindowByTabHandle(nint tabHandle)
    {
        if (tabHandle == 0) return null;
        return _windowEntryDict.TryGetValue(tabHandle, out InternetExplorer? foundWindow) ? foundWindow : null;
    }
    private static string[]? GetSelectedItems(InternetExplorer window)
    {
        var selectedItems = (window.Document as ShellFolderView)!.SelectedItems();
        var count = selectedItems.Count;
        if (count == 0) return null;

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = selectedItems.Item(i).Name;
        }

        return result;
    }
    private static void SelectItems(InternetExplorer window, string[]? names)
    {
        if (names == null || names.Length == 0) return;

        if (window.Document is not ShellFolderView document) return;

        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            object item = document.Folder.ParseName(name);
            if (item == null) continue;
            document.SelectItem(ref item, 1);
        }
    }
    private static string GetLocation(InternetExplorer window)
    {
        var path = window.LocationURL;
        if (!string.IsNullOrWhiteSpace(path)) return path;

        // Recycle Bin, This PC, etc
        path = ((window.Document as ShellFolderView)!.Folder as Folder2)!.Self.Path;
        return path.StartsWith(":") ? $"shell:{path}" : path;
    }
    private static string NormalizeLocation(string location)
    {
        if (location.IndexOf('%') > -1)
            location = Environment.ExpandEnvironmentVariables(location);

        if (location.StartsWith("{", StringComparison.Ordinal))
            location = $"shell:::{location}";

        return location.Trim(' ', '\n', '\'', '"');
    }

    private async Task MonitorExplorerProcess()
    {
        var cancellationToken = _processMonitorCts?.Token ?? CancellationToken.None;
        Process[] explorerProcesses;
        do
        {
            explorerProcesses = Process.GetProcessesByName("explorer");
            if (explorerProcesses.Length > 0) break;

            await Task.Delay(1000).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
        }
        while (explorerProcesses.Length == 0);

        foreach (var process in explorerProcesses)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    DisposeShellObjects();

                    if (cancellationToken.IsCancellationRequested) return;
                    if (!_created) return;

                    InitializeShellObjects();
                };
            }
            catch
            {
                //
            }
        }
    }
    private void CancelMonitorToken()
    {
        try
        {
            _processMonitorCts?.Cancel();
            _processMonitorCts?.Dispose();
        }
        catch
        {
            //
        }
    }
    private void InitializeShellObjects()
    {
        CancelMonitorToken();
        _processMonitorCts = new CancellationTokenSource();
        Task.Run(MonitorExplorerProcess);

        _shellPathComparer = new ShellPathComparer();
        _staTaskScheduler = new StaTaskScheduler();
        _shellWindows = new ShellWindows();

        // Hook the global "WindowRegistered" event
        _windowRegisteredHandler = OnShellWindowRegistered;
        _shellWindows.WindowRegistered += _windowRegisteredHandler;

        // Hook the global "OBJECT_SHOW" event
        _eventObjectShowHookCallback = OnWindowShown;
        _eventObjectShowHookId = WinApi.SetWinEventHook(WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW, 0, _eventObjectShowHookCallback, 0, 0, 0);

        // Hook the event handlers for already-open windows
        var count = _shellWindows.Count;
        for (var i = 0; i < count; i++)
        {
            if (_shellWindows.Item(i) is not InternetExplorer window) continue;

            var windowInfo = new WindowInfo();
            _windowEntryDict.Add(window, windowInfo);

            _ = GetTabHandle(window);
            HookWindowEvents(window, windowInfo);
        }
    }
    private void DisposeShellObjects()
    {
        CancelMonitorToken();
        // Unhook global event
        if (_windowRegisteredHandler != null)
        {
            _shellWindows.WindowRegistered -= _windowRegisteredHandler;
            _windowRegisteredHandler = null;
        }
        if (_eventObjectShowHookCallback != null)
        {
            WinApi.UnhookWinEvent(_eventObjectShowHookId);
            _eventObjectShowHookCallback = null;
        }

        // Unsubscribe from each InternetExplorer instance's events
        foreach (var (window, windowInfo) in _windowEntryDict)
        {
            // Unsubscribe
            if (windowInfo.OnQuitHandler != null)
                window.OnQuit -= windowInfo.OnQuitHandler;

            // Release the COM object
            Marshal.ReleaseComObject(window);
        }
        _windowEntryDict.Clear();

        // Release the ShellWindows COM object
        Marshal.ReleaseComObject(_shellWindows);

        _shellPathComparer.Dispose();

        _staTaskScheduler.Dispose();
    }

    public void Dispose()
    {
        DisposeShellObjects();

        _created = false;
        GC.SuppressFinalize(this);
    }
}