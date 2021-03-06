﻿// Copyright (c) Paul Harrington.  All Rights Reserved.  Licensed under the MIT License.  See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

using Task = System.Threading.Tasks.Task;

// I believe it's safe to suppress the analyzer warning about accessing the settings
// store only from the main thread. Switching to the UI thread everywhere would
// diminish the benefit of asynchronous expansion by tying up the UI thread.
#pragma warning disable VSTHRD010 // Invoke single-threaded types on Main thread

namespace SettingsStoreExplorer
{
    public enum Scope
    {
        Config = __VsEnclosingScopes.EnclosingScopes_Configuration,
        User = __VsEnclosingScopes.EnclosingScopes_UserSettings,
        Remote = __VsEnclosingScopes2.EnclosingScopes_Remote,
        Roaming
    }

    /// <summary>
    /// Base class for all items in the settings store data-model.
    /// </summary>
    [DebuggerDisplay("Name = {Name} Path = {Path}")]
    public abstract class SettingsStoreItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected SettingsStoreItem(SettingsStoreSubCollection parent, string name)
        {
            Parent = parent;
            _name = name;
        }

        public SettingsStoreSubCollection Parent { get; }

        private static string CombinePaths(string path1, string path2, char separatorChar) 
            => string.IsNullOrEmpty(path1) ? path2 : (path1 + separatorChar + path2);

        /// <summary>
        /// Path to this settings collection, not including the root node.
        /// </summary>
        public string Path => MakePath(Root.SeparatorChar);

        /// <summary>
        /// Recursive step of <see cref="Path"/> that takes a separator character.
        /// </summary>
        /// <param name="separatorChar">The separator character.</param>
        /// <returns>The path.</returns>
        private string MakePath(char separatorChar) => Parent == null ? "" : CombinePaths(Parent.MakePath(separatorChar), Name, separatorChar);

        /// <summary>
        /// Full path to this settings collection, including the root node.
        /// </summary>
        public string FullPath => MakeFullPath(Root.SeparatorChar);


        /// <summary>
        /// Recursive step of <see cref="FullPath"/> that takes a separator character.
        /// </summary>
        /// <param name="separatorChar">The separator character.</param>
        /// <returns>The full path.</returns>
        private string MakeFullPath(char separatorChar) => Parent == null ? Name : CombinePaths(Parent.MakeFullPath(separatorChar), Name, separatorChar);

        /// <summary>
        /// The root item. All items have a root (either "Config" or "User").
        /// </summary>
        public RootSettingsStore Root
        {
            get
            {
                SettingsStoreItem node;
                for (node = this; node.Parent != null; node = node.Parent)
                {
                }

                return node as RootSettingsStore;
            }
        }

        /// <summary>
        /// Backing field for <see cref="Name" />
        /// </summary>
        private string _name;

        /// <summary>
        /// The node's actual name in the settings store.
        /// </summary>
        public string Name
        {
            get => _name;
            private set
            {
                // Support for rename
                if (UpdateProperty(ref _name, value))
                {
                    NotifyPropertyChanged(nameof(Path));
                    NotifyPropertyChanged(nameof(FullPath));
                }
            }
        }

        /// <summary>
        /// Rename the item.
        /// </summary>
        /// <param name="newName"></param>
        public void Rename(string newName)
        {
            if (!CanRename)
            {
                throw new InvalidOperationException("Cannot rename this item.");
            }

            Name = newName;
        }

        /// <summary>
        /// Indicates whether the item can be renamed.
        /// </summary>
        protected virtual bool CanRename => true;

        /// <summary>
        /// Update the backing field of a property and fire notification if necessary.
        /// The default equality comparer is used to determine if the value has changed.
        /// </summary>
        /// <typeparam name="T">Type of the value being updated.</typeparam>
        /// <param name="field">The field to update.</param>
        /// <param name="newValue">The new value.</param>
        /// <param name="propertyName">The name of the property being updated.</param>
        /// <returns>true if the property was updated.</returns>
        protected bool UpdateProperty<T>(ref T field, T newValue, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, newValue))
            {
                return false;
            }

            field = newValue;
            NotifyPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Notify listeners of a property changed. The notification may be
        /// posted asynchronously if the caller is not on the main thread.
        /// </summary>
        /// <param name="propertyName">The name of the property that has changed.</param>
        protected void NotifyPropertyChanged(string propertyName)
        {
            if (ThreadHelper.CheckAccess())
            {
                InvokePropertyChanged(propertyName);
            }
            else
            {
#pragma warning disable VSTHRD001 // Avoid legacy thread switching APIs
                ThreadHelper.Generic.BeginInvoke(() => InvokePropertyChanged(propertyName));
#pragma warning restore VSTHRD001 // Avoid legacy thread switching APIs
            }
        }

        /// <summary>
        /// Fire the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that has changed.</param>
        private void InvokePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>
        /// Overriding ToString allows text-search matching.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;
    }

    /// <summary>
    /// A property in the settings store.
    /// </summary>
    public sealed class SettingsStoreProperty : SettingsStoreItem
    {
        private SettingsStoreProperty(SettingsStoreSubCollection parent, string name, __VsSettingsType type, object value) : base(parent, name)
        {
            Type = type;
            _value = value;
        }

        public static SettingsStoreProperty CreateInstance(SettingsStoreSubCollection parent, string name, __VsSettingsType type)
        {
            var initialValue = GetInitialValue(parent, name, type);
            return new SettingsStoreProperty(parent, name, type, initialValue);
        }

        private static object GetInitialValue(SettingsStoreSubCollection parent, string name, __VsSettingsType type)
        {
            var store = parent.Root.SettingsStore;
            var path = parent.Path;

            switch (type)
            {
                case __VsSettingsType.SettingsType_String:
                    return store.GetString(path, name);

                case __VsSettingsType.SettingsType_Int:
                    return store.GetUint32(path, name);

                case __VsSettingsType.SettingsType_Int64:
                    return store.GetUint64(path, name);

                case __VsSettingsType.SettingsType_Binary:
                    return store.GetByteArray(path, name);

                default:
                    return null;
            }
        }

        public __VsSettingsType Type { get; }

        private object _value;
        public object Value { get => _value; set => UpdateProperty(ref _value, value); }
        public string CollectionPath => Parent.Path;
    }

    /// <summary>
    /// A sub-collection in the settings store.
    /// </summary>
    public class SettingsStoreSubCollection : SettingsStoreItem
    {
        private ObservableCollection<SettingsStoreSubCollection> _subCollections;
        private ObservableCollection<SettingsStoreProperty> _properties;

        public SettingsStoreSubCollection(SettingsStoreSubCollection parent, string name) : base(parent, name)
        {
        }

        public ObservableCollection<SettingsStoreSubCollection> SubCollections => LazyInitializer.EnsureInitialized(ref _subCollections, SubCollectionsValueFactory);

        private ObservableCollection<SettingsStoreSubCollection> SubCollectionsValueFactory()
            => new ObservableCollection<SettingsStoreSubCollection>(CreateSubCollections());

        public ObservableCollection<SettingsStoreProperty> Properties => LazyInitializer.EnsureInitialized(ref _properties, PropertiesValueFactory);

        private ObservableCollection<SettingsStoreProperty> PropertiesValueFactory()
        {
            Task.Run(() => PopulatePropertiesAsync()).Forget();
            return new ObservableCollection<SettingsStoreProperty>();
        }

        protected virtual IEnumerable<SettingsStoreSubCollection> CreateSubCollections()
        {
            // We don't need to know the full collection yet, just whether the collection
            // is empty or not so that the tree view knows whether to display the expander.
            var store = Root.SettingsStore;

            // It is important that GetSubCollectionNames doesn't do eager evaluation,
            // otherwise it defeats the placeholder optimization.
            var names = store.GetSubCollectionNames(Path);
            if (names.Any())
            {
                // Create a single placeholder that knows how to expand its parent.
                yield return new SettingsStoreSubCollectionPlaceholder(this);
            }
        }

        /// <summary>
        /// Populate the SubCollections collection, replacing any placeholder item.
        /// </summary>
        /// <returns>The new collection.</returns>
        internal async Task<IEnumerable<SettingsStoreSubCollection>> ExpandSubCollectionsAsync()
        {
            var subCollections = GenerateSubCollections();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // The Interlocked.CompareExchange handles the case when this is called during a refresh.
            // See ExpandCollectionAsync in SettingsStoreViewModel.
            if (_subCollections != null ||
                Interlocked.CompareExchange(ref _subCollections, new ObservableCollection<SettingsStoreSubCollection>(subCollections), null) != null)
            {
                // Replace placeholder.
                UpdateSubCollections(subCollections);
            }

            return subCollections;
        }

        /// <summary>
        /// Enumerate the sub collections of this collection and create new view models for each.
        /// </summary>
        /// <returns>The list of sub collections.</returns>
        private List<SettingsStoreSubCollection> GenerateSubCollections()
        {
            var store = Root.SettingsStore;
            var path = Path;

            var subCollections = new List<SettingsStoreSubCollection>();
            foreach (var subCollectionName in store.GetSubCollectionNames(path))
            {
                subCollections.Add(new SettingsStoreSubCollection(this, subCollectionName));
            }

            return subCollections;
        }

        /// <summary>
        /// Refresh the SubCollections observable collection.
        /// </summary>
        /// <param name="subCollections">The new list of sub collections.</param>
        private void UpdateSubCollections(IEnumerable<SettingsStoreSubCollection> subCollections)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Subtle: If you've just added a new collection on a node that wasn't yet expanded, then the _subCollections list
            // will contain an item that is selected and we want to keep that way. We should be careful not to destroy it, otherwise
            // selection will be lost.

            // First remove the placeholder. If it's there, it'll always be the first one.
            if (_subCollections.Count > 0 && _subCollections[0] is SettingsStoreSubCollectionPlaceholder)
            {
                _subCollections.RemoveAt(0);
            }

            // If there's still one left, then we have to preserve it.
            if (_subCollections.Count == 1)
            {
                var preservedItemPath = _subCollections[0].Path;
                bool foundSelected = false;

                foreach (var subCollection in subCollections)
                {
                    if (foundSelected)
                    {
                        // Add to end of the list.
                        _subCollections.Add(subCollection);
                    }
                    else if (preservedItemPath.Equals(subCollection.Path, StringComparison.Ordinal))
                    {
                        foundSelected = true;
                    }
                    else
                    {
                        // Insert before the preserved item.
                        _subCollections.Insert(_subCollections.Count - 1, subCollection);
                    }
                }
            }
            else
            {
                _subCollections.AddRange(subCollections);
            }
        }

        private async Task PopulatePropertiesAsync()
        {
            var store = Root.SettingsStore;
            var path = Path;

            var properties = new List<SettingsStoreProperty>();
            foreach (var name in store.GetPropertyNames(path))
            {
                var type = store.GetPropertyType(path, name);
                properties.Add(SettingsStoreProperty.CreateInstance(this, name, type));
            }

            properties.Sort((p, q) => StringComparer.OrdinalIgnoreCase.Compare(p.Name, q.Name));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _properties.ReplaceAll(properties);
        }

        /// <summary>
        /// A place-holder for an unexpanded sub-collection. Initially it has just one
        /// item but, when the <see cref="CreateSubCollections"/> method is called,
        /// the sub-collection is replaced, asynchronously, with the real value.
        /// </summary>
        private sealed class SettingsStoreSubCollectionPlaceholder : SettingsStoreSubCollection
        {
            public SettingsStoreSubCollectionPlaceholder(SettingsStoreSubCollection parent) : base(parent, "Please wait...")
            {
            }

            /// <summary>
            /// When WPF really needs to know the SubCollections (because we expanded the node
            /// and it's bound to the name), this is our time to replace the placeholder with
            /// real values.
            /// </summary>
            /// <returns>An empty enumeration, always. The parent's subcollection is expanded
            /// in the background.</returns>
            protected override IEnumerable<SettingsStoreSubCollection> CreateSubCollections()
            {
                Task.Run(() => Parent.ExpandSubCollectionsAsync()).Forget();
                return Array.Empty<SettingsStoreSubCollection>();
            }

            protected override bool CanRename => false;
        }
    }

    /// <summary>
    /// A root node in the settings store.
    /// </summary>
    [DebuggerDisplay("Name = {Name} Root")]
    public sealed class RootSettingsStore : SettingsStoreSubCollection
    {
        public RootSettingsStore(IVsSettingsManager settingsManager, Scope enclosingScope, string name) : base(null, name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnclosingScope = enclosingScope;
            ErrorHandler.ThrowOnFailure(settingsManager.GetReadOnlySettingsStore((uint)enclosingScope, out var settingsStore));
            SettingsStore = settingsStore;
        }

        public RootSettingsStore(IVsSettingsStore settingsStore, Scope enclosingScope, string name) : base(null, name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            EnclosingScope = enclosingScope;
            SettingsStore = settingsStore;
        }

        public Scope EnclosingScope { get; }

        public IVsSettingsStore SettingsStore { get; }

        protected override bool CanRename => false;

        internal char SeparatorChar => EnclosingScope == Scope.Roaming ? '.' : '\\';
    }

    /// <summary>
    /// The view-model for the settings store. Represents the "Config" and "User" trees.
    /// </summary>
    public sealed class SettingsStoreViewModel
    {
        public SettingsStoreSubCollection[] Roots { get; set; }

        public SettingsStoreViewModel(IVsSettingsManager settingsManager, ISettingsManager roamingSettingsManager)
        {
            Roots = new SettingsStoreSubCollection[] {
                new RootSettingsStore(settingsManager, Scope.Config, "Config"),
                new RootSettingsStore(settingsManager, Scope.User, "User"),
                new RootSettingsStore(settingsManager, Scope.Remote, "Remote"),
                new RootSettingsStore(new RoamingSettingsStore(roamingSettingsManager), Scope.Roaming, "Roaming")
            };
        }

        /// <summary>
        /// Force the expansion of collections along the given full path from root.
        /// Expansion is asynchronous. When it's finished, the <paramref name="onExpanded"/> callback is called on the UI thread.
        /// </summary>
        /// <param name="fullPath">The full path to expand.</param>
        /// <param name="onExpanded">A callback that is called when expansion is complete.</param>
        public void RequestExpansion(string fullPath, Action<SettingsStoreSubCollection> onExpanded)
            => Task.Run(() => ExpandCollectionAsync(fullPath, onExpanded)).Forget();

        private async Task ExpandCollectionAsync(string fullPath, Action<SettingsStoreSubCollection> onExpanded)
        {
            var index = 0;
            var rootName = NextPart(fullPath, ref index);

            SettingsStoreSubCollection subCollection = null;

            // Find the root
            foreach (var root in Roots)
            {
                if (root.Name == rootName)
                {
                    subCollection = root;
                    break;
                }
            }

            if (subCollection != null)
            {
                // Expand down the tree.
                while (index >= 0)
                {
                    // ExpandSubCollectionsAsync switches to the UI thread, but we want to continue on the worker thread.
                    // There is an implicit "ConfigureAwait(true)" on the awaiter which does that.
                    var subCollections = await subCollection.ExpandSubCollectionsAsync();
                    ThreadHelper.ThrowIfOnUIThread();

                    var subCollectionName = NextPart(fullPath, ref index);
                    var childSubCollection = subCollections.FirstOrDefault(sub => sub.Name == subCollectionName);
                    if (childSubCollection == null)
                    {
                        break;
                    }

                    subCollection = childSubCollection;
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            onExpanded(subCollection);
        }

        /// <summary>
        /// Extract the next part of the path starting at the given index.
        /// </summary>
        /// <param name="path">The full path.</param>
        /// <param name="index">The starting index. Will be updated on exit. If less than zero, then there are no more parts.</param>
        /// <returns>The next part of the path.</returns>
        private static string NextPart(string path, ref int index)
        {
            var separatorIndex = path.IndexOf('\\', index);
            if (separatorIndex < 0)
            {
                // Last segment
                path = path.Substring(index);
                index = separatorIndex;
            }
            else
            {
                path = path.Substring(index, separatorIndex - index);
                index = separatorIndex + 1;
            }

            return path;
        }
    }
}
