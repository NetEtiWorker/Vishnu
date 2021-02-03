﻿using NetEti.MVVMini;
using System;
using System.Windows.Threading;
using Vishnu.Interchange;

namespace Vishnu.ViewModel
{
    /// <summary>
    /// Basisklasse für alle Vishnu-ViewModels.
    /// Erbt von ObservableObject u.a. INotifyPropertyChanged
    /// und implementiert IVishnuViewModel.
    /// </summary>
    /// <remarks>
    /// File: VishnuViewModelBase.cs
    /// Autor: Erik Nagel
    ///
    /// 22.05.2015 Erik Nagel: erstellt
    /// </remarks>
    public class VishnuViewModelBase : ObservableObject, IVishnuViewModel
    {
        /// <summary>
        /// Das ReturnObject der zugeordneten LogicalNode.
        /// </summary>
        public virtual Result Result
        {
            get
            {
                return this._result;
            }
            set
            {
                if (this._result != value)
                {
                    this._result = value;
                    this.RaisePropertyChanged("Result");
                }
            }
        }

        /// <summary>
        /// Das Parent-Control.
        /// </summary>
        public DynamicUserControlBase ParentView
        {
            get
            {
                return this._parentView;
            }
            set
            {
                if (this._parentView != value)
                {
                    this._parentView = value;
                    this.ParentViewToBL(this._parentView);
                    this.RaisePropertyChanged("ParentView");
                }
            }
        }

        /// <summary>
        /// Bindung an ein optionales, spezifisches User-ViewModel.
        /// </summary>
        public object UserDataContext
        {
            get
            {
                return this._userDataContext;
            }
            set
            {
                if (this._userDataContext != value)
                {
                    this._userDataContext = value;
                    this.RaisePropertyChanged("UserDataContext");
                }
            }
        }

        /// <summary>
        /// Eindeutiger GlobalUniqueIdentifier.
        /// Wird im Konstruktor vergeben und fließt in die überschriebene Equals-Methode ein.
        /// Dadurch wird erreicht, dass nach Reload von Teilen des LogicalTaskTree und erneutem
        /// Reload von vorherigen Ständen des LogicalTaskTree Elemente des ursprünglich 
        /// gecachten VisualTree fälschlicherweise anstelle der neu geladenen Elemente in den
        /// neuen VisualTree übernommen werden.
        /// </summary>
        public string VisualTreeCacheBreaker
        {
            get
            {
                return this._visualTreeCacheBreaker;
            }
            private set
            {
                if (this._visualTreeCacheBreaker != value)
                {
                    this._visualTreeCacheBreaker = value;
                    this.RaisePropertyChanged("VisualTreeCacheBreaker");
                    this.RaisePropertyChanged("DebugNodeInfos");
                }
            }
        }

        /// <summary>
        /// Vergibt einen neuen GlobalUniqueIdentifier für den VisualTreeCacheBreaker.
        /// </summary>
        public virtual void Invalidate()
        {
            this.VisualTreeCacheBreaker = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Kann überschrieben werden, um das Parent-Control
        /// in der Geschäftslogik zu speichern.
        /// </summary>
        /// <param name="parentView">Das Parent-Control.</param>
        protected virtual void ParentViewToBL(DynamicUserControlBase parentView)
        {
        }

        /// <summary>
        /// Konstruktor - setzt den VisualTreeCacheBreaker.
        /// </summary>
        public VishnuViewModelBase()
        {
            this._visualTreeCacheBreaker = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Vergleicht den Inhalt dieses LogicalNodeViewModels nach logischen Gesichtspunkten
        /// mit dem Inhalt eines übergebenen LogicalNodeViewModels.
        /// </summary>
        /// <param name="obj">Das LogicalNodeViewModel zum Vergleich.</param>
        /// <returns>True, wenn das übergebene LogicalNodeViewModel inhaltlich gleich diesem ist.</returns>
        public override bool Equals(object obj)
        {
            if (obj == null || this.GetType() != obj.GetType())
            {
                return false;
            }
            return (Object.ReferenceEquals(this, obj));
        }

        /// <summary>
        /// Erzeugt einen Hashcode für dieses LogicalNodeViewModel.
        /// </summary>
        /// <returns>Integer mit Hashwert.</returns>
        public override int GetHashCode()
        {
            return base.GetHashCode() + this.VisualTreeCacheBreaker.GetHashCode();
        }

        private Interchange.Result _result;
        private object _userDataContext;
        private DynamicUserControlBase _parentView;
        private string _visualTreeCacheBreaker;
    }
}
