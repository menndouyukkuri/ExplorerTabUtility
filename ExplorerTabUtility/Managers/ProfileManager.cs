﻿using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Collections.Generic;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Forms;
using ExplorerTabUtility.WinAPI;

namespace ExplorerTabUtility.Managers;

public class ProfileManager
{
    // Saved state (persistent)
    private readonly List<HotKeyProfile> _savedProfiles = [];
    // Temporary state (for editing)
    private readonly List<HotKeyProfile> _tempProfiles = [];
    private readonly FlowLayoutPanel _profilePanel;

    public event Action? ProfilesChanged;
    public event Action? KeybindingsHookStarted;
    public event Action? KeybindingsHookStopped;

    public ProfileManager(FlowLayoutPanel profilePanel)
    {
        _profilePanel = profilePanel;

        LoadSavedProfiles();
        RefreshFlowPanel();
    }

    private void LoadSavedProfiles()
    {
        try
        {
            var profiles = JsonSerializer.Deserialize<List<HotKeyProfile>>(SettingsManager.HotKeyProfiles);
            if (profiles == null) return;

            _savedProfiles.Clear();
            _savedProfiles.AddRange(profiles);

            // Create temporary copies
            _tempProfiles.Clear();
            foreach (var p in _savedProfiles)
            {
                _tempProfiles.Add(p.Clone());
            }
        }
        catch
        {
            // Invalid JSON or deserialization error
        }
    }

    public void AddProfile(HotKeyProfile? profile = null)
    {
        var newProfile = profile?.Clone() ?? new HotKeyProfile();
        _tempProfiles.Add(newProfile);
        _profilePanel.Controls.Add(new HotKeyProfileControl(newProfile, RemoveProfile, KeybindingHookStarted, KeybindingHookStopped));
    }

    private void RemoveProfile(HotKeyProfile profile)
    {
        _tempProfiles.Remove(profile);
        var control = FindControlByProfile(profile);
        if (control != null)
            _profilePanel.Controls.Remove(control);
    }

    private void RefreshFlowPanel()
    {
        _profilePanel.SuspendLayout();
        _profilePanel.SuspendDrawing();
        _profilePanel.Controls.Clear();

        foreach (var profile in _tempProfiles)
        {
            _profilePanel.Controls.Add(new HotKeyProfileControl(profile, RemoveProfile, KeybindingHookStarted, KeybindingHookStopped));
        }

        _profilePanel.ResumeDrawing();
        _profilePanel.ResumeLayout();
    }
    private void KeybindingHookStarted() => KeybindingsHookStarted?.Invoke();
    private void KeybindingHookStopped() => KeybindingsHookStopped?.Invoke();

    public void SetProfileEnabledFromTray(HotKeyProfile profile, bool enabled)
    {
        // Find and update in saved profiles (for tray menu)
        var savedProfile = _savedProfiles.First(p => p.Id == profile.Id);
        savedProfile.IsEnabled = enabled;

        // Find and update in temp profiles (for panel)
        var tempProfile = _tempProfiles.FirstOrDefault(p => p.Id == profile.Id);
        if (tempProfile == null) return;
        
        tempProfile.IsEnabled = enabled;
        var control = FindControlByProfile(tempProfile);
        if (control != null) control.IsEnabled = enabled;
    }

    public IReadOnlyList<HotKeyProfile> GetProfiles() => _savedProfiles.AsReadOnly();
    public IEnumerable<HotKeyProfile> GetKeyboardProfiles() => _savedProfiles.Where(p => !p.IsMouse);
    public IEnumerable<HotKeyProfile> GetMouseProfiles() => _savedProfiles.Where(p => p.IsMouse);

    public void SaveProfiles()
    {
        // Remove profiles that don't have any hotkeys
        _tempProfiles.RemoveAll(p => p.HotKeys == null || p.HotKeys.Length == 0);

        // Update saved profiles
        _savedProfiles.Clear();
        foreach (var profile in _tempProfiles)
        {
            _savedProfiles.Add(profile.Clone());
        }

        // Save to settings
        SettingsManager.HotKeyProfiles = JsonSerializer.Serialize(_savedProfiles);
        ProfilesChanged?.Invoke();
    }

    public void ImportProfiles(string jsonString)
    {
        try
        {
            var importedList = JsonSerializer.Deserialize<List<HotKeyProfile>>(jsonString);
            if (importedList == null) return;

            _tempProfiles.Clear();
            foreach (var profile in importedList)
            {
                _tempProfiles.Add(profile.Clone());
            }

            RefreshFlowPanel();
        }
        catch
        {
            // Invalid JSON or deserialization error
        }
    }

    public string ExportProfiles() => JsonSerializer.Serialize(_savedProfiles);

    private HotKeyProfileControl? FindControlByProfile(HotKeyProfile profile)
    {
        return _profilePanel.Controls
            .OfType<HotKeyProfileControl>()
            .FirstOrDefault(c => c.Tag?.Equals(profile) == true);
    }
}
