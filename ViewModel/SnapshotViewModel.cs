﻿using System;
using System.Collections.Generic;
using System.Windows.Input;
using NetEti.MVVMini;
using System.Windows;
using LogicalTaskTree;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Vishnu.Interchange;

namespace Vishnu.ViewModel
{
    /// <summary>
    /// ViewModel für einen Snapshot eines externen Trees.
    /// </summary>
    /// <remarks>
    /// File: SnapshotViewModel.cs
    /// Autor: Erik Nagel
    ///
    /// 12.11.2013 Erik Nagel: erstellt
    /// </remarks>
    public class SnapshotViewModel : LogicalNodeViewModel
    {
        #region public members

        #region published members

        /// <summary>
        /// Zeitpunkt der letzten Aktualisierung des Snapshots.
        /// </summary>
        public string SnapshotTime
        {
            get
            {
                return (this._myLogicalNode as Snapshot).Timestamp.ToString();
                // return (this._myLogicalNode as Snapshot).Timestamp.ToString()
                //     + " (" + DateTime.Now.ToString("HH.mm.ss") + ")";
            }
            set
            {
                this.RaisePropertyChanged("SnapshotTime");
            }
        }

        /// <summary>
        /// True, wenn der Snapshot nicht geladen werden konnte
        /// und durch einen Default-Snapshot ersetzt wurde.
        /// </summary>
        public bool IsDefaultSnapshot
        {
            get
            {
                return (this._myLogicalNode as Snapshot).IsDefaultSnapshot;
            }
            set
            {
                this.RaisePropertyChanged("IsDefaultSnapshot");
            }
        }

        /// <summary>
        /// Bei True befindet sich diese NodeList innerhalb eies Snapshots.
        /// </summary>
        public bool IsInSnapshot
        {
            get
            {
                return this._myLogicalNode.IsInSnapshot;
            }
            set
            {
                this.RaisePropertyChanged("IsInSnapshot");
            }
        }

        /// <summary>
        /// Herkunft des Snapshots.
        /// </summary>
        public string SnapshotPath
        {
            get
            {
                return (this._myLogicalNode as Snapshot).SnapshotPath;
            }
            set
            {
                this.RaisePropertyChanged("SnapshotPath");
            }
        }

        /// <summary>
        /// Command für den Refresh des Snapshots.
        /// </summary>
        public ICommand RefreshSnapshot { get { return this._btnRefreshSnapshotRelayCommand; } }

        /// <summary>
        /// Command für das Umschalten der Tree-Darstellung.
        /// </summary>
        public ICommand SwitchTaskTreeView { get { return this._btnSwitchTaskTreeViewRelayCommand; } }

        #endregion published members

        /// <summary>
        /// Konstruktor
        /// </summary>
        /// <param name="parent">Der übergeordnete ViewModel-Knoten.</param>
        /// <param name="snapshot">Der zugeordnete Knoten aus dem LogicalTaskTree.</param>
        /// <param name="lazyLoadChildren">Bei True werden die Kinder erst beim Öffnen des TreeView-Knotens nachgeladen.</param>
        /// <param name="uIMain">Das Root-FrameworkElement zu diesem ViewModel.</param>
        /// <param name="logicalTaskTreeViewModel">Das dem Root-Knoten übergeordnete ViewModel (nur beim Root-Job ungleich null).</param>
        public SnapshotViewModel(OrientedTreeViewModelBase logicalTaskTreeViewModel, LogicalNodeViewModel parent, LogicalTaskTree.Snapshot snapshot, bool lazyLoadChildren, FrameworkElement uIMain)
          : base(logicalTaskTreeViewModel, parent, snapshot, lazyLoadChildren, uIMain)
        {
            this._btnRefreshSnapshotRelayCommand = new RelayCommand(RefreshSnapshotExecute, canRefreshSnapshotExecute);
            this._btnSwitchTaskTreeViewRelayCommand = new RelayCommand(switchTaskTreeViewExecute, canSwitchTaskTreeViewExecute);
            (this._myLogicalNode as Snapshot).SnapshotRefreshed -= SnapshotViewModel_SnapshotRefreshed;
            (this._myLogicalNode as Snapshot).SnapshotRefreshed += SnapshotViewModel_SnapshotRefreshed;
            this.RaisePropertyChanged("IsSnapshotDummy");
        }

        #endregion public members

        #region private members

        private RelayCommand _btnRefreshSnapshotRelayCommand;
        private RelayCommand _btnSwitchTaskTreeViewRelayCommand;

        private void RefreshSnapshotExecute(object parameter)
        {
            if (!this.IsRefreshing)
            {
                this.IsRefreshing = true;
                ((this._myLogicalNode) as Snapshot).RefreshSnapshot();
            }
        }

        private bool canRefreshSnapshotExecute()
        {
            return true;
        }

        private void switchTaskTreeViewExecute(object parameter)
        {
            switch (this.RootLogicalTaskTreeViewModel.TreeOrientationState)
            {
                case TreeOrientation.AlternatingHorizontal:
                    this.RootLogicalTaskTreeViewModel.TreeOrientationState = TreeOrientation.Horizontal;
                    break;
                case TreeOrientation.Vertical:
                    this.RootLogicalTaskTreeViewModel.TreeOrientationState = TreeOrientation.AlternatingVertical;
                    break;
                case TreeOrientation.Horizontal:
                    this.RootLogicalTaskTreeViewModel.TreeOrientationState = TreeOrientation.Vertical;
                    break;
                case TreeOrientation.AlternatingVertical:
                    this.RootLogicalTaskTreeViewModel.TreeOrientationState = TreeOrientation.AlternatingHorizontal;
                    break;
                default:
                    this.RootLogicalTaskTreeViewModel.TreeOrientationState = TreeOrientation.AlternatingHorizontal;
                    break;
            }
        }

        private bool canSwitchTaskTreeViewExecute()
        {
            return true;
        }

        private void SnapshotViewModel_SnapshotRefreshed(object sender)
        {
            this.Dispatcher.BeginInvoke(new Action(() =>
            {
                if ((this._myLogicalNode as Snapshot).WasDefaultSnapshot)
                {
                    this.FullTreeRefresh();
                }
                else
                {
                    if (!this.LeanTreeRefresh())
                    {
                        // (this._myLogicalNode as Snapshot).SaveLastSnapshotForDebugging();
                        this.FullTreeRefresh();
                        return;
                    }
                }
                this.RaisePropertyChanged("IsDefaultSnapshot");
                this.RaisePropertyChanged("IsInSnapshot");
            }));
        }

        #endregion private members

    }
}
