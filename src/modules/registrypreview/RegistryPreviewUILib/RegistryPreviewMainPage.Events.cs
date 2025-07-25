﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.WinUI.UI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation.Metadata;
using Windows.Storage;

namespace RegistryPreviewUILib
{
    public sealed partial class RegistryPreviewMainPage : Page
    {
        private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Indicator if we loaded/reloaded/saved a file and need to skip TextChanged event one time.
        // (Solves the problem that enabling the event handler fires it one time.)
        private static bool editorContentChangedScripted;

        /// <summary>
        /// Event that is will prevent the app from closing if the "save file" flag is active
        /// </summary>
        public void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // Only block closing if the REG file has been edited but not yet saved
            if (saveButton.IsEnabled)
            {
                // if true, the app will not close
                args.Handled = true;

                // ask the user if they want to save, discard or cancel the close; strings must be loaded here and passed to avoid timing issues
                HandleDirtyClosing(
                    resourceLoader.GetString("YesNoCancelDialogTitle"),
                    resourceLoader.GetString("YesNoCancelDialogContent"),
                    resourceLoader.GetString("YesNoCancelDialogPrimaryButtonText"),
                    resourceLoader.GetString("YesNoCancelDialogSecondaryButtonText"),
                    resourceLoader.GetString("YesNoCancelDialogCloseButtonText"));
            }
        }

        /// <summary>
        /// Event that gets fired after the visual tree has been fully loaded; the app opens the reg file from here so it can show a message box successfully
        /// </summary>
        private async void GridPreview_Loaded(object sender, RoutedEventArgs e)
        {
            // static flag to track whether the Visual Tree is ready - if the main Grid has been loaded, the tree is ready.
            visualTreeReady = true;

            // Check to see if the REG file was opened and parsed successfully
            if (await OpenRegistryFile(_appFileName) == false)
            {
                if (File.Exists(_appFileName))
                {
                    // Allow Refresh and Edit to be enabled because a broken Reg file might be fixable
                    UpdateToolBarAndUI(false, true, true);
                    _updateWindowTitleFunction(resourceLoader.GetString("InvalidRegistryFileTitle"));
                    MonacoEditor.TextChanged += MonacoEditor_TextChanged;
                    return;
                }
                else
                {
                    UpdateToolBarAndUI(false, false, false);
                    _updateWindowTitleFunction(string.Empty);
                }
            }
            else
            {
                MonacoEditor.TextChanged += MonacoEditor_TextChanged;
            }

            MonacoEditor.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// New button action: Ask to save last changes and reset editor content to reg header only
        /// </summary>
        private async void NewButton_Click(object sender, RoutedEventArgs e)
        {
            // Check to see if the current file has been saved
            if (saveButton.IsEnabled)
            {
                ContentDialog contentDialog = new ContentDialog()
                {
                    Title = resourceLoader.GetString("YesNoCancelDialogTitle"),
                    Content = resourceLoader.GetString("YesNoCancelDialogContent"),
                    PrimaryButtonText = resourceLoader.GetString("YesNoCancelDialogPrimaryButtonText"),
                    SecondaryButtonText = resourceLoader.GetString("YesNoCancelDialogSecondaryButtonText"),
                    CloseButtonText = resourceLoader.GetString("YesNoCancelDialogCloseButtonText"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                // Use this code to associate the dialog to the appropriate AppWindow by setting
                // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    contentDialog.XamlRoot = this.Content.XamlRoot;
                }

                ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();
                switch (contentDialogResult)
                {
                    case ContentDialogResult.Primary:
                        // Save, then continue the new action
                        if (!AskFileName(string.Empty) ||
                            !SaveFile())
                        {
                            return;
                        }

                        break;
                    case ContentDialogResult.Secondary:
                        // Don't save and continue the new action!
                        break;
                    default:
                        // Don't open the new action!
                        return;
                }
            }

            // mute the TextChanged handler to make for clean UI
            MonacoEditor.TextChanged -= MonacoEditor_TextChanged;

            // reset editor, file info and ui.
            _appFileName = string.Empty;
            ResetEditorAndFile();

            // disable buttons that do not make sense
            UpdateUnsavedFileState(false);
            refreshButton.IsEnabled = false;

            // restore the TextChanged handler
            ButtonAction_RestoreTextChangedEvent();
        }

        /// <summary>
        /// Uses a picker to select a new file to open
        /// </summary>
        private async void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            // Check to see if the current file has been saved
            if (saveButton.IsEnabled)
            {
                ContentDialog contentDialog = new ContentDialog()
                {
                    Title = resourceLoader.GetString("YesNoCancelDialogTitle"),
                    Content = resourceLoader.GetString("YesNoCancelDialogContent"),
                    PrimaryButtonText = resourceLoader.GetString("YesNoCancelDialogPrimaryButtonText"),
                    SecondaryButtonText = resourceLoader.GetString("YesNoCancelDialogSecondaryButtonText"),
                    CloseButtonText = resourceLoader.GetString("YesNoCancelDialogCloseButtonText"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                // Use this code to associate the dialog to the appropriate AppWindow by setting
                // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    contentDialog.XamlRoot = this.Content.XamlRoot;
                }

                ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();
                switch (contentDialogResult)
                {
                    case ContentDialogResult.Primary:
                        // Save, then continue the file open
                        if (!AskFileName(string.Empty) ||
                            !SaveFile())
                        {
                            return;
                        }

                        break;
                    case ContentDialogResult.Secondary:
                        // Don't save and continue the file open!
                        break;
                    default:
                        // Don't open the new file!
                        return;
                }
            }

            // Pull in a new REG file - we have to use the direct Win32 method because FileOpenPicker crashes when it's
            // called while running as admin
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
            string filename = OpenFilePicker.ShowDialog(
                windowHandle,
                resourceLoader.GetString("FilterRegistryName") + '\0' + "*.reg" + '\0' + resourceLoader.GetString("FilterAllFiles") + '\0' + "*.*" + '\0' + '\0',
                resourceLoader.GetString("OpenDialogTitle"));

            if (filename == string.Empty || File.Exists(filename) == false)
            {
                return;
            }

            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(filename);

            if (storageFile != null)
            {
                // mute the TextChanged handler to make for clean UI
                MonacoEditor.TextChanged -= MonacoEditor_TextChanged;

                // update file name
                _appFileName = storageFile.Path;
                UpdateToolBarAndUI(await OpenRegistryFile(_appFileName));

                // disable the Save button as it's a new file
                UpdateUnsavedFileState(false);

                // Restore the event handler as we're loaded
                ButtonAction_RestoreTextChangedEvent();
            }
        }

        /// <summary>
        /// Saves the currently opened file in place
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!AskFileName(string.Empty))
            {
                return;
            }

            // save and update window title
            // error handling and ui update happens in SaveFile() method
            _ = SaveFile();
        }

        /// <summary>
        /// Uses a picker to save out a copy of the current reg file
        /// </summary>
        private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            // mute the TextChanged handler to make for clean UI
            MonacoEditor.TextChanged -= MonacoEditor_TextChanged;

            if (!AskFileName(_appFileName) || !SaveFile())
            {
                return;
            }

            UpdateToolBarAndUI(await OpenRegistryFile(_appFileName));

            // restore the TextChanged handler
            ButtonAction_RestoreTextChangedEvent();
        }

        /// <summary>
        /// Reloads the current REG file from storage
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Check to see if the current file has been saved
            if (saveButton.IsEnabled)
            {
                ContentDialog contentDialog = new ContentDialog()
                {
                    Title = resourceLoader.GetString("YesNoCancelDialogTitle"),
                    Content = resourceLoader.GetString("ReloadDialogContent"),
                    PrimaryButtonText = resourceLoader.GetString("ReloadDialogPrimaryButtonText"),
                    CloseButtonText = resourceLoader.GetString("ReloadDialogCloseButtonText"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                // Use this code to associate the dialog to the appropriate AppWindow by setting
                // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    contentDialog.XamlRoot = this.Content.XamlRoot;
                }

                ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();
                switch (contentDialogResult)
                {
                    case ContentDialogResult.Primary:
                        // Don't save and continue the reload action!
                        break;
                    default:
                        // Don't continue the reload action!
                        return;
                }
            }

            // mute the TextChanged handler to make for clean UI
            MonacoEditor.TextChanged -= MonacoEditor_TextChanged;

            // reload the current Registry file and update the toolbar accordingly.
            UpdateToolBarAndUI(await OpenRegistryFile(_appFileName), true, true);

            // disable the Save button as it's a new file
            UpdateUnsavedFileState(false);

            // restore the TextChanged handler
            ButtonAction_RestoreTextChangedEvent();
        }

        /// <summary>
        /// Opens the Registry Editor; UAC is handled by the request to open
        /// </summary>
        private void RegistryButton_Click(object sender, RoutedEventArgs e)
        {
            // pass in an empty string as we have no file to open
            OpenRegistryEditor(string.Empty);
        }

        /// <summary>
        /// Opens the Registry Editor and tries to set "last used"; UAC is handled by the request to open
        /// </summary>
        private void RegistryJumpToKeyButton_Click(object sender, RoutedEventArgs e)
        {
            // Get the selected Key, if there is one
            TreeViewNode currentNode = treeView.SelectedNode;
            if (currentNode != null)
            {
                // since there is a valid node, get the FullPath of the key that was selected
                string key = ((RegistryKey)currentNode.Content).FullPath;

                // it's impossible to directly open a key via command-line option, so we must override the last remember key
                Microsoft.Win32.Registry.SetValue(@"HKEY_Current_User\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", key);
            }

            // pass in an empty string as we have no file to open
            OpenRegistryEditor(string.Empty);
        }

        /// <summary>
        /// Merges the currently saved file into the Registry Editor; UAC is handled by the request to open
        /// </summary>
        private async void WriteButton_Click(object sender, RoutedEventArgs e)
        {
            // Check to see if the current file has been saved
            if (saveButton.IsEnabled)
            {
                ContentDialog contentDialog = new ContentDialog()
                {
                    Title = resourceLoader.GetString("YesNoCancelDialogTitle"),
                    Content = resourceLoader.GetString("YesNoCancelDialogContent"),
                    PrimaryButtonText = resourceLoader.GetString("YesNoCancelDialogPrimaryButtonText"),
                    SecondaryButtonText = resourceLoader.GetString("YesNoCancelDialogSecondaryButtonText"),
                    CloseButtonText = resourceLoader.GetString("YesNoCancelDialogCloseButtonText"),
                    DefaultButton = ContentDialogButton.Primary,
                };

                // Use this code to associate the dialog to the appropriate AppWindow by setting
                // the dialog's XamlRoot to the same XamlRoot as an element that is already present in the AppWindow.
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    contentDialog.XamlRoot = this.Content.XamlRoot;
                }

                ContentDialogResult contentDialogResult = await contentDialog.ShowAsync();
                switch (contentDialogResult)
                {
                    case ContentDialogResult.Primary:
                        // Save, then continue the merge action
                        if (!AskFileName(string.Empty) ||
                            !SaveFile())
                        {
                            return;
                        }

                        break;
                    case ContentDialogResult.Secondary:
                        // Don't save and continue the merge action!
                        UpdateUnsavedFileState(false);
                        break;
                    default:
                        // Don't merge the file!
                        return;
                }
            }

            // pass in the filename so we can edit the current file
            OpenRegistryEditor(_appFileName);
        }

        /// <summary>
        /// Opens the currently saved file in the PC's default REG file editor (often Notepad)
        /// </summary>
        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            // use the REG file's filename and verb so we can respect the selected editor
            Process process = new Process();
            process.StartInfo.FileName = string.Format(CultureInfo.InvariantCulture, "\"{0}\"", _appFileName);
            process.StartInfo.Verb = "Edit";
            process.StartInfo.UseShellExecute = true;

            try
            {
                process.Start();
            }
            catch
            {
                ShowMessageBox(
                    resourceLoader.GetString("ErrorDialogTitle"),
                    resourceLoader.GetString("FileEditorError"),
                    resourceLoader.GetString("OkButtonText"));
            }
        }

        /// <summary>
        /// Trigger that fires when a node in treeView is clicked and which populates dataGrid
        /// Can also be fired from elsewhere in the code
        /// </summary>
        private void TreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            TreeViewItemInvokedEventArgs localArgs = args as TreeViewItemInvokedEventArgs;
            TreeViewNode treeViewNode = null;

            // if there are no args, the mouse didn't get clicked but we want to believe it did
            if (args != null)
            {
                treeViewNode = args.InvokedItem as TreeViewNode;
            }
            else
            {
                treeViewNode = treeView.SelectedNode;
            }

            // Update the toolbar button for the tree
            registryJumpToKeyButton.IsEnabled = CheckTreeForValidKey();

            // Grab the object that has Registry data in it from the currently selected treeView node
            RegistryKey registryKey = (RegistryKey)treeViewNode.Content;

            // no matter what happens, clear the ListView of items on each click
            ClearTable();

            // if there's no ListView items stored for the selected node, dataGrid is clear so get out now
            if (registryKey.Tag == null)
            {
                return;
            }

            // if there WAS something in the Tag property, cast it to a list and Populate the ListView
            ArrayList arrayList = (ArrayList)registryKey.Tag;
            listRegistryValues = new List<RegistryValue>();

            for (int i = 0; i < arrayList.Count; i++)
            {
                RegistryValue listViewItem = (RegistryValue)arrayList[i];
                listRegistryValues.Add(listViewItem);
            }

            // create a new binding for dataGrid and reattach it, updating the rows
            Binding listRegistryValuesBinding = new Binding { Source = listRegistryValues };
            dataGrid.SetBinding(DataGrid.ItemsSourceProperty, listRegistryValuesBinding);
        }

        /// <summary>
        /// When the text in editor changes, reload treeView and possibly dataGrid and reset the save button
        /// </summary>
        private void MonacoEditor_TextChanged(object sender, EventArgs e)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                RefreshRegistryFile();
                if (!editorContentChangedScripted)
                {
                    UpdateUnsavedFileState(true);
                }

                editorContentChangedScripted = false;
            });
        }

        /// <summary>
        /// Sets indicator for programatic text change and adds text changed handler
        /// </summary>
        /// <remarks>
        /// Use this always, if button actions temporary disable the text changed event
        /// </remarks>
        private void ButtonAction_RestoreTextChangedEvent()
        {
            // Solves the problem that enabling the event handler fires it one time.
            // These one time fired event would causes wrong unsaved changes state.
            editorContentChangedScripted = true;
            MonacoEditor.TextChanged += MonacoEditor_TextChanged;
        }

        // Commands to show data preview
        public void ButtonExtendedPreview_Click(object sender, RoutedEventArgs e)
        {
            var data = ((Button)sender).DataContext as RegistryValue;
            InvokeExtendedDataPreview(data);
        }

        public void MenuExtendedPreview_Click(object sender, RoutedEventArgs e)
        {
            var data = ((MenuFlyoutItem)sender).DataContext as RegistryValue;
            InvokeExtendedDataPreview(data);
        }

        private async void InvokeExtendedDataPreview(RegistryValue valueData)
        {
            // Only one content dialog can be open at the same time and multiple instances of data preview can crash the app.
            if (_dialogSemaphore.CurrentCount == 0)
            {
                return;
            }

            try
            {
                // Lock ui and request dialog lock
                _dialogSemaphore.Wait();
                ChangeCursor(gridPreview, true);

                await ShowExtendedDataPreview(valueData.Name, valueData.Type, valueData.Value);
            }
            catch
            {
#if DEBUG
                throw;
#endif
            }
            finally
            {
                // Unblock ui and release dialog lock
                ChangeCursor(gridPreview, false);
                _dialogSemaphore.Release();
            }
        }
    }
}
