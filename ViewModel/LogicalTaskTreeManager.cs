﻿using LogicalTaskTree;
using NetEti.ApplicationControl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Vishnu.Interchange;

namespace Vishnu.ViewModel
{
    /// <summary>
    /// LogicalTaskTreeManager bietet Funktionen zum Verwalten von LogicalTaskTrees:
    ///   - Mergen eines neu geladenen Trees in einen bestehenden aktiven Tree,
    ///   - Rekursives Loggen wichtiger Eigenschaften von Teilbäumen eines LogicalTaskTrees.
    /// </summary>
    /// <remarks>
    /// Author: Erik Nagel, NetEti
    ///
    /// 01.01.2021 Erik Nagel, NetEti: created.
    /// </remarks>
    internal static class LogicalTaskTreeManager
    {

        /// <summary>
        /// Loggt den für Debug-Zwecke zum aktuellen Zeitpunkt.
        /// Das Logging beginnt ab dem übergebenen logicalNodeViewModel, wenn es ein JobListViewModel ist,
        /// ansonsten ab der nächsten diesem übergeordneten Joblist.
        /// Wenn der Zusatzparameter "fromTop" auf true steht, wird der gesamte Tree geloggt.
        /// </summary>
        /// <param name="beyondRootJobList">Ein LogicalNodeViewModel innerhalb des Trees.</param>
        /// <param name="fromTop">Bei true wird der gesamte Tree geloggt.</param>
        public static void LogTaskTree(LogicalNodeViewModel beyondRootJobList, bool fromTop = false)
        {
            JobListViewModel rootJobListViewModel;
            if (beyondRootJobList is JobListViewModel)
            {
                rootJobListViewModel = beyondRootJobList as JobListViewModel;
            }
            else
            {
                rootJobListViewModel = beyondRootJobList.GetTopRootJobListViewModel();
            }
            JobList rootJobList = rootJobListViewModel.GetLogicalNode() as JobList;

            JobListViewModel treeTopRootJobListViewModel = rootJobListViewModel.GetTopRootJobListViewModel() ?? rootJobListViewModel as JobListViewModel;
            JobList TopRootJobList = treeTopRootJobListViewModel.GetLogicalNode() as JobList;
            LogicalTaskTreeManager._allReferencedObjects = new List<object>();
            treeTopRootJobListViewModel.Traverse(CollectReferencedObjects, LogicalTaskTreeManager._allReferencedObjects);

            List<string> allTreeInfos = new List<string>();
            object result1 = rootJobListViewModel.Traverse(ListTreeElement, allTreeInfos);
            string bigMessage = String.Join(System.Environment.NewLine, allTreeInfos);
            InfoController.GetInfoPublisher().Publish(rootJobListViewModel, Environment.NewLine
                + "--- A C T I V E  T R E E ------------------------------------------------------------------------------------------------------", InfoType.NoRegex);
            Thread.Sleep(100);
            InfoController.GetInfoPublisher().Publish(rootJobListViewModel, Environment.NewLine + bigMessage, InfoType.NoRegex);
            Thread.Sleep(100);
            InfoController.GetInfoPublisher().Publish(rootJobListViewModel, Environment.NewLine
                + "-------------------------------------------------------------------------------------------------------------------------------", InfoType.NoRegex);
            InfoController.FlushAll();
        }

        static LogicalTaskTreeManager()
        {
            LogicalTaskTreeManager._reloadedSingleNodes = new List<string>();
        }

        private static Dictionary<string, LogicalNodeViewModel> _shadowVMFinder;
        private static Dictionary<string, LogicalNodeViewModel> _treeVMFinder;
        private static List<object> _allReferencedObjects;

        // Enthält Pfade von SingleNodes, die im Zuge eines Reload des LogicalTaskTrees neu eingebaut wurden.
        // Bei der späteren Ausführung der Run-Methode dieser Nodes dürfen dynamisch geladene DLLs einmalig
        // nicht aus dem Cache genommen werden.
        private static List<string> _reloadedSingleNodes { get; set; }

        /// <summary>
        /// Verarbeitet zwei übergebene Trees aus LogicNodeViewModels:
        /// "activeTree" und "newTree".
        /// Im Prinzip soll nach der Verarbeitung in LogicalTaskTreeMerger Vishnu ohne Unterbrechung mit
        /// einer Repräsentation des newTrees weiterarbeiten. Hierbei sollen nur neu hinzugekommene oder
        /// grundlegend veränderte Knoten neu gestartet werden müssen.
        /// Aktive oder unveränderte Knoten im activeTree, die auch im newTree vorkommen, sollen möglichst
        /// unangetastet bleiben.
        /// Das heißt, dass ihre aktuellen Verarbeitungszustände, logischen Werte, Trigger, Logger, etc.
        /// erhalten bleiben (sie insbesondere nicht neu gestartet werden müssen).
        /// Aktive Knoten im activeTree, die nicht mehr im newTree vorkommen, müssen geordnet beendet und
        /// freigegeben werden.
        /// Besonders zu beachten:
        ///   - Differenzierte Verarbeitung von JobLists, bei denen sich die LogicalExpression geändert hat;
        ///     Hier müssen die JobLists getauscht werden, aber evtl. gleich gebliebene Kinder unangetastet
        ///     bleiben.
        ///   - Merging von für JobLists und Tree globalen Arrays und Dictionaries:
        ///     Job
        ///       ...
        ///           #region tree globals
        ///   
        ///           /// Liste von internen Triggern für einen jobPackage.Job.
        ///           public Dictionary «string, Dictionary«string, TriggerShell»» EventTriggers { get; set; }
        ///                                 |                  |
        ///                                 |                  +-> Quelle, z.B SubJob1 oder CheckTreeEvents
        ///                                 |
        ///                                 +-> Events string, z.B. "AnyException|LastNotNullLogicalChanged"
        ///             EventTriggers werden nach oben in die TopRootJobList propagiert.
        ///   
        ///           /// Liste von externen Arbeitsroutinen für einen jobPackage.Job.
        ///           /// Ist ein Dictionary mit WorkerShell-Arrays zu aus
        ///           /// Knoten-Id + ":" + TreeEvents-String gebildeten Keys.
        ///           public Workers Workers { get; private set; }
        ///             public Dictionary«string, Dictionary«string, WorkerShell[]»» WorkersDictionary { get; set; }
        ///                                |                  |
        ///                                |                  +-> Quelle, z.B. any SQLServer queryingJob
        ///                                |
        ///                                +-> einzelnes Event, z.B. "AnyException" oder "LastNotNullLogicalChanged"
        ///             Workers werden nicht nach oben in die TopRootJobList propagiert,
        ///             sondern sind für jede JobList spezifisch.
        ///   
        ///           #endregion tree globals
        ///           ...
        ///   
        ///     JobList
        ///       ...
        ///           #region tree globals
        ///   
        ///           // Der externe Job mit logischem Ausdruck und u.a. Dictionary der Worker.
        ///           public Job Job { get; set; }
        ///   
        ///           nur temporär /// Dictionary von externen Prüfroutinen für einen jobPackage.Job mit Namen als Key.
        ///                        /// Wird als Lookup für unaufgelöste JobConnector-Referenzen genutzt.
        ///                        public Dictionary«string, NodeCheckerBase» AllCheckersForUnreferencingNodeConnectors { get; set; }
        ///                                           |
        ///                                           +-> Checker-Name, z.B. "Datum"
        ///                        AllCheckersForUnreferencingNodeConnectors werden nach oben in die TopRootJobList propagiert,
        ///                        aber bei der Auflösung von unreferenzierenden NodeConnectoren nach und nach entfernt.
        ///                        Das heißt, dass in der TopRootJobList AllCheckersForUnreferencingNodeConnectors nach der initialen
        ///                        Verarbeitung leer sein muss, ansonsten sind NodeConnectoren ohne gültige Referenz übrig geblieben.
        ///   
        ///           nur temporär /// Liste von NodeConnectoren, die beim Parsen der Jobs noch nicht aufgelöst
        ///                        /// werden konnten.
        ///                        public List«NodeConnector» UnsatisfiedNodeConnectors;
        ///   
        ///           /// Dictionary von externen Prüfroutinen für eine JobList, die nicht in
        ///           /// der LogicalExpression referenziert werden; Checker, die ausschließlich
        ///           /// über ValueModifier angesprochen werden.
        ///           public Dictionary«string, NodeCheckerBase» TreeExternalCheckers { get; set; }
        ///                              |
        ///                              +-> Checker-Id, z.B. "Datum" (NodeName wird zur Id)
        ///             TreeExternalCheckers werden nicht nach oben in die TopRootJobList propagiert,
        ///             sondern sind für jede JobList spezifisch.
        ///   
        ///           /// Liste von externen SingleNodes für die TopRootJobList, die in keiner
        ///           /// der LogicalExpressions referenziert werden; Nodes, die ausschließlich
        ///           /// über NodeConnectoren angesprochen werden.
        ///           public List«SingleNode» TreeExternalSingleNodes { get; set; }
        ///                        |
        ///                        +-> SingleNode Binary
        ///             TreeExternalSingleNodes werden nicht nach oben in die TopRootJobList propagiert,
        ///             sondern sind für jede JobList spezifisch.
        ///   
        ///           /// Cache zur Beschleunigung der Verarbeitung von TreeEvents
        ///           /// bezogen auf EventTrigger.
        ///           public List«string» TriggerRelevantEventCache;
        ///                        |
        ///                        +-> einzelnes Event, z.B. "AnyException" oder "LastNotNullLogicalChanged"
        ///             TriggerRelevantEventCache wird nach oben in die TopRootJobList propagiert.
        ///             
        ///           /// Cache zur Beschleunigung der Verarbeitung von TreeEvents
        ///           /// bezogen auf Worker.
        ///           public List«string» WorkerRelevantEventCache;
        ///                        |
        ///                        +-> einzelnes Event, z.B. "AnyException" oder "LastNotNullLogicalChanged"
        ///             Hinweis: Vishnu fügt allen JobLists in WorkerRelevantEventCache das Event 'Breaked' hinzu. 
        ///             WorkerRelevantEventCache wird nach oben in die TopRootJobList propagiert.
        ///   
        ///           /// Cache zur Beschleunigung der Verarbeitung von TreeEvents
        ///           /// bezogen auf Logger.
        ///           public List«string» LoggerRelevantEventCache;
        ///                        |
        ///                        +-> einzelnes Event, z.B. "AnyException" oder "LastNotNullLogicalChanged"
        ///             LoggerRelevantEventCache wird nach oben in die TopRootJobList propagiert.
        ///   
        ///           /// Dictionary von JobLists mit ihren Namen als Keys.
        ///           public Dictionary«string, JobList> JobsByName;
        ///                              |
        ///                              +-> einzelne Job-Id, z.B. "CheckServers" oder "SubJob1"
        ///             JobsByName werden nach oben in die TopRootJobList propagiert.
        ///   
        ///           /// Dictionary von LogicalNodes mit ihren Namen als Keys.
        ///           public Dictionary«string, LogicalNode» NodesByName;
        ///                              |
        ///                              +-> einzelne Knoten-Id, z.B. "Datum" oder "Check_D"
        ///             NodesByName werden nicht nach oben in die TopRootJobList propagiert,
        ///             sondern sind für jede JobList spezifisch.
        ///   
        ///           /// Dictionary von LogicalNodes mit ihren Namen als Keys.
        ///           public Dictionary«string, LogicalNode» TreeRootLastChanceNodesByName;
        ///                              |
        ///                              +-> einzelne Knoten-Id, z.B. "Datum" oder "Check_D"
        ///             TreeRootLastChanceNodesByName werden nach oben in die TopRootJobList propagiert.
        ///   
        ///           /// Dictionary von LogicalNodes mit ihren Ids als Keys.
        ///           public Dictionary«string, LogicalNode» NodesById;
        ///                              |
        ///                              +-> einzelne Knoten-Id, z.B. "Datum" oder "Check_D" oder "Child_1_@26"
        ///             NodesById werden nach oben in die TopRootJobList propagiert.
        ///   
        ///           #endregion tree globals
        /// 
        /// </summary>
        internal static void MergeTaskTrees(JobListViewModel activeTree, JobListViewModel newTree)
        {
            try
            {
                // Trees indizieren.
                LogicalTaskTreeManager._shadowVMFinder = new Dictionary<string, LogicalNodeViewModel>();
                newTree.Traverse(IndexTreeElement, LogicalTaskTreeManager._shadowVMFinder);
                LogicalTaskTreeManager._treeVMFinder = new Dictionary<string, LogicalNodeViewModel>();
                activeTree.Traverse(IndexTreeElement, LogicalTaskTreeManager._treeVMFinder);

                // Da im aktiven Tree Knoten ausgetauscht werden, kann es zu null-Referenzen kommen.
                // Deshalb werden ab hier alle kritischen Aktionen pausiert, bis der Reload durch ist.
                activeTree.GetLogicalNode()?.ProhibitSnapshots();

                // activeTree.GetLogicalNode()?.PauseTree(); // TEST 20210202
                // newTree.GetLogicalNode()?.PauseTree(); // TEST 20210202
                // InfoController.Say(String.Format($"#RELOAD# Trees paused"));
                // InfoController.FlushAll();

                // Durchläuft den aktiven Tree und prüft parallel den neu geladenen "ShadowTree" auf Gleichheit, bzw. Ungleichheit.
                // Da hinzugekommene oder ausgetauschte Knoten immer auch über Unterschiede in den LogicalExpressions der
                // übergeordneten JobLists gefunden werden, bleiben keine Veränderungen unbemerkt.
                // Bei differierenden JobLists wird aus pragmatischen Erwägungen zusätzlich noch versucht, eventuell gleich
                // gebliebene Children zu erhalten, damit bei großen Jobs nicht alle Children neu gestartet werden müssen.
                // Darüber hinaus möglicherweise noch gleiche Teilbäume an unterschiedlichen Hierarchie-Ebenen werden nicht
                // gesucht, der Nutzen wäre eher gering bei ungleich höherem Aufwand (Ehrlich gesagt, ist mir das zu kompliziert).
                activeTree.Traverse(DiffTreeElement, LogicalTaskTreeManager._shadowVMFinder);

                // Abschließend muss noch die Jobs-Ansicht aktualisiert werden (LogicalTaskJobGroupsControl).

                //Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                //{
                activeTree.TreeParams.ViewModelRoot.RefreshDependentAlternativeViewModels();
                //}));
            }
            finally
            {
                newTree.Dispose();
                VishnuAssemblyLoader.ClearCache(); // Sorgt dafür, dass alle DLLs neu geladen werden.
                // activeTree.GetLogicalNode()?.ResumeTree(); TEST 20210202
                activeTree.GetLogicalNode()?.AllowSnapshots();
            }
        }

        /// <summary>
        /// Vergleicht die übergebene ExpandableNode mit ihrem Pendant in dem Shadow-Tree.
        /// </summary>
        /// <param name="depth">Nullbasierter Zähler der Rekursionstiefe eines Knotens im LogicalTaskTree.</param>
        /// <param name="expandableNode">Basisklasse eines ViewModel-Knotens im LogicalTaskTree.</param>
        /// <param name="userObject">Ein beliebiges durchgeschliffenes UserObject (hier: Dictionary&lt;string, LogicalNodeViewModel&gt;).</param>
        /// <returns>Das Dictionary&lt;string, LogicalNodeViewModel&gt; oder null.</returns>
        internal static object DiffTreeElement(int depth, IExpandableNode expandableNode, object userObject)
        {
            Dictionary<string, LogicalNodeViewModel> vmFinder = (Dictionary<string, LogicalNodeViewModel>)userObject;
            if (vmFinder.ContainsKey(expandableNode.Path))
            {
                LogicalNodeViewModel shadowNodeVM = vmFinder[expandableNode.Path];
                LogicalNodeViewModel activeNodeVM = expandableNode as LogicalNodeViewModel;
                LogicalNode shadowNode = shadowNodeVM.GetLogicalNode();
                LogicalNode activeNode = activeNodeVM.GetLogicalNode();
                if (shadowNode is JobList && activeNode is JobList)
                {
                    LogicalTaskTreeManager.TransferGlobals(shadowNodeVM as JobListViewModel, activeNodeVM as JobListViewModel, shadowNode as JobList, activeNode as JobList);
                }
                if (shadowNode.Equals(activeNode))
                {
                    return userObject; // Weiter im Tree
                }
                else
                {
                    LogicalTaskTreeManager.TransferNode(shadowNodeVM, activeNodeVM, shadowNode, activeNode);
                }
            }
            else
            {
                throw new ApplicationException(String.Format($"Unerwarteter Suchfehler auf {expandableNode.Path}."));
            }

            return null; // Bricht die Rekursion für diesen Zweig ab.
        }

        private static void TransferNode(LogicalNodeViewModel shadowNodeVM, LogicalNodeViewModel activeNodeVM,
            LogicalNode shadowNode, LogicalNode activeNode)
        {
            InfoController.Say(String.Format($"#RELOAD# Transferring Node {shadowNodeVM.Path} from ShadowTree to Tree."));

            if (activeNode.Mother == null)
            {
                // throw new ApplicationException("Der Top-Job kann nicht im laufenden Betrieb ausgetauscht werden!"); TODO: dies auch ermöglichen.
                InfoController.Say(String.Format($"#RELOAD# Not transferring Root-Node {shadowNodeVM.Path} from ShadowTree to Tree."));
                return;
            }

            LogicalNodeViewModel activeParentVM = activeNodeVM.Parent;
            NodeParent activeParent = activeNode.Mother as NodeParent;
            LogicalNodeViewModel shadowParentVM = shadowNodeVM.Parent;
            NodeParent shadowParent = shadowNode.Mother as NodeParent;
            int nodeIndex = GetParentNodeIndex(activeNode, activeParent);

            // commonChildIndices: Key=activeNode.Children, Value=shadowNode.Children
            Dictionary<int, int> commonChildIndices = null;
            if (shadowNode is JobList && activeNode is JobList)
            {
                commonChildIndices = LogicalTaskTreeManager.FindEqualJobListChildrenAndSetDummyConstants(shadowNodeVM as JobListViewModel, activeNodeVM as JobListViewModel, shadowNode as JobList, activeNode as JobList);
            }

            try
            {
                // Knoten aus dem aktiven Tree sichern scheint nicht nötig,
                // wird schon durch activeNode und activeNodeVM referenziert

                // Knoten im aktiven Tree durch Knoten im Shadow-Tree ersetzen.
                shadowNodeVM.Parent = activeParentVM;
                shadowNodeVM.RootLogicalTaskTreeViewModel = activeNodeVM.RootLogicalTaskTreeViewModel;

                shadowNode.Mother = activeParent;
                shadowNode.RootJobList = activeNode.RootJobList;
                shadowNode.TreeRootJobList = activeNode.TreeRootJobList;

                activeParentVM.Children[nodeIndex].ReleaseBLNode(); // nur freigeben, nicht disposen
                activeParent.ReleaseChildAt(nodeIndex); // nur freigeben, nicht disposen
                activeParent.SetChildAt(nodeIndex, shadowNode);
                activeParentVM.Children[nodeIndex] = shadowNodeVM;
                activeParentVM.Children[nodeIndex].SetBLNode(shadowNode, true);

                // Der Run auf die übernommene ShadowNode darf erst an dieser Stelle erfolgen, also wenn die shadowNode schon im aktiven Tree hängt.
                // Anderenfalls bekommt der übergeordnete Knoten den Lauf des neuen Knoten und sein LogicalResult nicht mit.
                activeParent.LastLogical = null;

                // activeParent.ResumeTree(); // TEST 20210202

                shadowNode.Run(new TreeEvent("UserRun", shadowNode.Id, shadowNode.Id, shadowNode.Name, shadowNode.Path, null, NodeLogicalState.None, null, null));
                InfoController.Say(String.Format($"#RELOAD# shadowNode im activeTree started"));
                Thread.Sleep(500); // Der Run braucht etwas, deshalb muss hier eine Wartezeit eingebaut werden.

                // activeParent.PauseTree(); // TEST 20210202
                // shadowParent.PauseTree(); // TEST 20210202

                Thread.Sleep(200);
                InfoController.Say(String.Format($"#RELOAD# {activeParentVM.TreeParams.Name}:{activeParentVM.Id} {(activeParentVM.VisualTreeCacheBreaker)} vor Invalidate()"));
                Thread.Sleep(200);
                activeParentVM.InitFromNode(shadowParentVM);
                Thread.Sleep(200);
                InfoController.Say(String.Format($"#RELOAD# {activeParentVM.TreeParams.Name}:{activeParentVM.Id} {(activeParentVM.VisualTreeCacheBreaker)} nach Invalidate()"));

                // Das shadowParentVM und der shadowParent halten noch Referenzen aufeinander
                // und auf den Shadow-Knoten, der gerade in den activeTree hinüberwandert.
                // Der Shadow-Tree wird in einem abschließenden Schritt in LogicalNodeViewModel disposed.
                // Deshalb müssen seine Verbindungen zu dem in den active-Tree wechselnden Knoten
                // vorher gekappt werden (sonst würde dieser gleich wieder mit disposed).
                // shadowParentVM.Children[nodeIndex].ReleaseBLNode(); // nicht erforderlich.
                shadowParentVM.ReleaseBLNode();
                shadowParentVM.Children[nodeIndex] = null;
                shadowParent.ReleaseChildAt(nodeIndex);

                if (commonChildIndices != null)
                {
                    // Hier wurde geade eine JobList in den aktiven Tree übernommen und gestartet, die an den Stellen, wo sie mit der
                    // ursprünglichen JobList gemeinsame Kinder hat, noch Dummy-Knoten enthält. Diese Dummy-Knoten müssen jetzt durch
                    // die ursprünglichen, noch laufenden Kinder der Original-(vor dem Austausch durch die Shadow-JobList)-JobList
                    // des aktiven Trees wieder ersetzt werden.
                    JobList activeJobList = activeNode as JobList;
                    JobList shadowJobList = shadowNode as JobList;
                    JobListViewModel shadowJobListVM = shadowNodeVM as JobListViewModel;
                    JobListViewModel activeJobListVM = activeNodeVM as JobListViewModel;
                    for (int i = 0; i < activeNode.Children.Count; i++) // activeNode (JobList) zeigt hier noch auf den ursprünglichen Knoten des activeTree
                    {
                        if (!commonChildIndices.ContainsKey(i))
                        {
                            // activeJobList.Children[i].Break(true); // Dieser Knoten fällt weg, Break würde aber wegen Tree-Pause nicht zurückkommen.
                            activeJobListVM.Children[i].Dispose();
                            activeJobList.FreeChildAt(i);
                        }
                        else
                        {

                            int j = commonChildIndices[i];

                            // Nicht abbrechen, die ShadowNode hängt schon im active-Tree und der Break würde sich nach oben fortpflanzen
                            //                       shadowJobList.Children[j].Break(true);
                            shadowJobListVM.Children[j].Dispose(); // Ersatzkonstante und Kinder freigeben.
                            shadowJobList.FreeChildAt(j); // Ersatzkonstante freigeben.

                            activeJobListVM.Children[i].Parent = shadowJobListVM;
                            activeJobListVM.Children[i].RootLogicalTaskTreeViewModel = shadowJobListVM.RootLogicalTaskTreeViewModel;

                            activeNode.Children[i].Mother = shadowJobList;
                            activeNode.Children[i].RootJobList = shadowJobList;
                            activeNode.Children[i].TreeRootJobList = shadowJobList.TreeRootJobList;

                            shadowJobList.SetChildAt(j, activeNode.Children[i]); // Referenziert den noch im activeTree laufenden
                                                                                 // Knoten und hängt sich in dessen Events ein.
                            shadowJobListVM.Children[j] = activeJobListVM.Children[i]; // Referenziert das noch im activeTree laufende ViewModel.
                            shadowJobListVM.Children[j].SetBLNode(activeNode.Children[i], true);
                            activeJobListVM.Children[i].Invalidate();

                            activeJobListVM.Children[i] = null;
                            activeJobList.ReleaseChildAt(i);

                            // shadowJobListVM.Children[j].RefreshVisualTreeCacheBreaker();
                        }
                    }
                }

                // == activeNodeVM: LogicalNodeViewModel activeVMtoDispose = activeParentVM.Children[nodeIndex];

                try
                {
                    LogicalTaskTreeManager.AdjustBranchRootJobListGlobals(shadowNode);
                }
                catch (Exception)
                {
                    throw;
                }

                // Der früher aktive Knoten sollte jetzt abgebrochen und isoliert sein, also komplett freigeben.
                activeNode = null;
                activeNodeVM.Dispose();
            }
            finally
            {
                // activeParent.ResumeTree();
            }

            // activeParentVM.FullTreeRefresh();
        }

        private static int GetParentNodeIndex(LogicalNode activeNode, NodeParent activeParent)
        {
            int nodeIndex = -1;
            for (int i = 0; i < activeParent.Children.Count; i++)
            {
                if (activeParent.Children[i].Equals(activeNode))
                {
                    nodeIndex = i;
                    break;
                }
            }
            if (nodeIndex < 0)
            {
                throw new ApplicationException(String.Format($"Parent-Index nicht gefunden, Knoten: {activeNode.Path}"));
            }
            return nodeIndex;
        }

        private static void TransferGlobals(JobListViewModel shadowNodeVM, JobListViewModel activeNodeVM, JobList shadowNode, JobList activeNode)
        {
            LogicalTaskTreeManager.AddNewJobListGlobals(shadowNode, activeNode);
            LogicalTaskTreeManager.RemoveOldJobListGlobals(shadowNode, activeNode);
        }

        private static Dictionary<int, int> FindEqualJobListChildrenAndSetDummyConstants(JobListViewModel shadowJobListVM, JobListViewModel activeJobListVM, JobList shadowJobList, JobList activeJobList)
        {
            Dictionary<int, int> commonChildIndices = new Dictionary<int, int>();  // Key=activeNode.Children, Value=shadowNode.Children

            // überflüssig, da die ViewModels jeweils eine Referenz auf ihre Business-Nodes ghalten:
            //     JobList tempNode = new JobList(shadowNode, shadowNode.GetTopRootJobList());
            //     tempVM.SetBLNode(tempNode, true);

            // Eventuelle gleichgebliebene direkte Nachkommen von logicalNode in shadowNode durch
            // Konstanten mit gleichen LastNotNullLogical ersetzen (wegen späterem Run auf shadowNode).
            // Die gesamte shadowNode inklusive Children wird in einem späteren Schritt auf den aktiven Tree übertragen.
            for (int i = 0; i < shadowJobList.Children.Count; i++)
            {
                for (int j = 0; j < activeJobList.Children.Count; j++)
                {
                    if (activeJobList.Children[j].Equals(shadowJobList.Children[i]))
                    {
                        commonChildIndices.Add(j, i); // Key=activeNode.Children, Value=shadowNode.Children
                        SingleNode constantDummyNode = new SingleNode("@BOOL." + activeJobList.Children[j].LastNotNullLogical.ToString(),
                            shadowJobList, shadowJobList.Children[i].RootJobList, shadowJobList.Children[i].TreeParams);

                        shadowJobListVM.Children[i].UnsetBLNode();
                        shadowJobList.FreeChildAt(i);
                        shadowJobList.SetChildAt(i, constantDummyNode);
                        shadowJobListVM.Children[i].SetBLNode(constantDummyNode, true);

                        break;
                    }
                }
            }

            return commonChildIndices;
        }

        /// <summary>
        /// Ändert bei aus dem ShadowTree in den aktiven Tree übernommenen Knoten Referenzen des activeTree auf den früheren Knoten
        /// unter gleichem Pfad auf den neuen Knoten. Muss auch für alle eventuellen Children des Knoten aufgerufen werden.
        /// Der Knoten muss vorher in den aktiven Tree übernommen worden sein, da die Verarbeitung im aktiven Tree erfolgen
        /// muss.
        /// </summary>
        /// <param name="newLogicalNode">Die neu in den Tree übernommene LogicalNode.</param>
        private static void AdjustBranchRootJobListGlobals(LogicalNode newLogicalNode)
        {
            newLogicalNode.Traverse(AdjustRootJobListGlobals);
        }

        /// <summary>
        /// Ändert bei aus dem ShadowTree in den aktiven Tree übernommenen Knoten Referenzen auf den früheren Knoten
        /// unter gleichem Pfad auf den neuen Knoten.
        /// </summary>
        private static object AdjustRootJobListGlobals(int depth, LogicalNode newLogicalNode, object userObject)
        {
            LogicalNode changingNode = newLogicalNode;
            do
            {
                newLogicalNode = newLogicalNode.RootJobList;
                LogicalTaskTreeManager.ChangeOldReferences(changingNode, newLogicalNode as JobList);
            } while (newLogicalNode.Mother != null);
            return null;
        }

        private static void ChangeOldReferences(LogicalNode changingNode, JobList treeJobList)
        {
            // Generell funktioniert foreach hier nicht, da u.U. die Auflistungen geändert werden.
            LogicalNodeViewModel shadowTreeVM = LogicalTaskTreeManager._shadowVMFinder[treeJobList.IdPath];
            JobList shadowJobList = shadowTreeVM.GetLogicalNode() as JobList;
            List<String> keys;
            try
            {
                keys = new List<string>(treeJobList.Job.EventTriggers.Keys);
                foreach (string key in keys)
                {
                    List<String> keyKeys = new List<string>(treeJobList.Job.EventTriggers[key].Keys);
                    foreach (string keyKey in keyKeys)
                    {
                        if (changingNode.Id == keyKey)
                        {
                            if (shadowJobList != null && shadowJobList.Job.EventTriggers.ContainsKey(key) && shadowJobList.Job.EventTriggers[key].ContainsKey(keyKey))
                            {
                                treeJobList.Job.EventTriggers[key][keyKey] = shadowJobList.Job.EventTriggers[key][keyKey];
                            }
                        }
                    }
                }

            }
            catch (Exception ex1)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.Job.WorkersDictionary.Keys);
                foreach (string key in keys)
                {
                    List<String> keyKeys = new List<string>(treeJobList.Job.WorkersDictionary[key].Keys);
                    foreach (string keyKey in keyKeys)
                    {
                        if (changingNode.Id == keyKey)
                        {
                            if (shadowJobList != null && shadowJobList.Job.WorkersDictionary.ContainsKey(key) && shadowJobList.Job.WorkersDictionary[key].ContainsKey(keyKey))
                            {
                                treeJobList.Job.WorkersDictionary[key][keyKey] = shadowJobList.Job.WorkersDictionary[key][keyKey];
                            }
                        }
                    }
                }

            }
            catch (Exception ex2)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.AllCheckersForUnreferencingNodeConnectors.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Name == key)
                    {
                        if (shadowJobList != null && shadowJobList.AllCheckersForUnreferencingNodeConnectors.ContainsKey(key))
                        {
                            treeJobList.AllCheckersForUnreferencingNodeConnectors[key] = shadowJobList.AllCheckersForUnreferencingNodeConnectors[key];
                        }
                    }
                }

            }
            catch (Exception ex3)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.TreeExternalCheckers.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Id == key)
                    {
                        if (shadowJobList != null && shadowJobList.TreeExternalCheckers.ContainsKey(key))
                        {
                            treeJobList.TreeExternalCheckers[key] = shadowJobList.TreeExternalCheckers[key];
                        }
                    }
                }

            }
            catch (Exception ex4)
            {
                throw;
            }
            /*
            foreach (SingleNode shadowNode in treeJobList.TreeExternalSingleNodes)
            {
                bool found = false;
                foreach (SingleNode treeNode in treeJobList.TreeExternalSingleNodes)
                {
                    if (treeNode.Equals(shadowNode))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    treeJobList.TreeExternalSingleNodes.Add(shadowNode);
                }
            }
            foreach (string key in treeJobList.TriggerRelevantEventCache)
            {
                if (!treeJobList.TriggerRelevantEventCache.Contains(key))
                {
                    treeJobList.TriggerRelevantEventCache.Add(key);
                }
            }
            foreach (string key in treeJobList.LoggerRelevantEventCache)
            {
                if (!treeJobList.LoggerRelevantEventCache.Contains(key))
                {
                    treeJobList.LoggerRelevantEventCache.Add(key);
                }
            }
            foreach (string key in treeJobList.WorkerRelevantEventCache)
            {
                if (!treeJobList.WorkerRelevantEventCache.Contains(key))
                {
                    treeJobList.WorkerRelevantEventCache.Add(key);
                }
            }
            */
            try
            {
                keys = new List<string>(treeJobList.JobsByName.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Id == key)
                    {
                        if (shadowJobList != null && shadowJobList.JobsByName.ContainsKey(key))
                        {
                            treeJobList.JobsByName[key] = shadowJobList.JobsByName[key];
                        }
                    }
                }

            }
            catch (Exception ex5)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.NodesByName.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Id == key)
                    {
                        if (shadowJobList != null && shadowJobList.NodesByName.ContainsKey(key))
                        {
                            treeJobList.NodesByName[key] = shadowJobList.NodesByName[key];
                        }
                    }
                }

            }
            catch (Exception ex6)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.TreeRootLastChanceNodesByName.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Id == key)
                    {
                        if (shadowJobList != null && shadowJobList.TreeRootLastChanceNodesByName.ContainsKey(key))
                        {
                            treeJobList.TreeRootLastChanceNodesByName[key] = shadowJobList.TreeRootLastChanceNodesByName[key];
                        }
                    }
                }

            }
            catch (Exception ex7)
            {
                throw;
            }
            try
            {
                keys = new List<string>(treeJobList.NodesById.Keys);
                foreach (string key in keys)
                {
                    if (changingNode.Id == key)
                    {
                        if (shadowJobList != null && shadowJobList.NodesById.ContainsKey(key))
                        {
                            treeJobList.NodesById[key] = shadowJobList.NodesById[key];
                        }
                    }
                }

            }
            catch (Exception ex8)
            {
                throw;
            }
        }

        private static void AddNewJobListGlobals(JobList shadowJobList, JobList activeJobList)
        {
            InfoController.Say(String.Format($"#RELOAD# Transferring Tree Globals from ShadowTree to Tree."));

            foreach (string key in shadowJobList.Job.EventTriggers.Keys)
            {
                if (!activeJobList.Job.EventTriggers.ContainsKey(key))
                {
                    activeJobList.Job.EventTriggers.Add(key, shadowJobList.Job.EventTriggers[key]);
                }
                else
                {
                    foreach (string keyKey in shadowJobList.Job.EventTriggers[key].Keys)
                    {
                        if (!activeJobList.Job.EventTriggers[key].ContainsKey(keyKey))
                        {
                            activeJobList.Job.EventTriggers[key].Add(keyKey, shadowJobList.Job.EventTriggers[key][keyKey]);
                        }
                    }
                }
            }
            foreach (string key in shadowJobList.Job.WorkersDictionary.Keys)
            {
                if (!activeJobList.Job.WorkersDictionary.ContainsKey(key))
                {
                    activeJobList.Job.WorkersDictionary.Add(key, shadowJobList.Job.WorkersDictionary[key]);
                }
                else
                {
                    foreach (string keyKey in shadowJobList.Job.WorkersDictionary[key].Keys)
                    {
                        if (!activeJobList.Job.WorkersDictionary[key].ContainsKey(keyKey))
                        {
                            activeJobList.Job.WorkersDictionary[key].Add(keyKey, shadowJobList.Job.WorkersDictionary[key][keyKey]);
                        }
                        else
                        {
                            List<WorkerShell> combinedTreeWorkers = activeJobList.Job.WorkersDictionary[key][keyKey].ToList();
                            foreach (WorkerShell keyKeyShadowWorker in shadowJobList.Job.WorkersDictionary[key][keyKey])
                            {
                                string shadowSlave = keyKeyShadowWorker.SlavePathName;
                                bool found = false;
                                foreach (WorkerShell keyKeyTreeWorker in activeJobList.Job.WorkersDictionary[key][keyKey])
                                {
                                    string treeSlave = keyKeyTreeWorker.SlavePathName;
                                    if (treeSlave == shadowSlave)
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found)
                                {
                                    combinedTreeWorkers.Add(keyKeyShadowWorker);
                                }
                            }
                            if (activeJobList.Job.WorkersDictionary[key][keyKey].Length < combinedTreeWorkers.Count)
                            {
                                activeJobList.Job.WorkersDictionary[key][keyKey] = combinedTreeWorkers.ToArray();
                            }
                        }
                    }
                }
            }
            foreach (string key in shadowJobList.AllCheckersForUnreferencingNodeConnectors.Keys)
            {
                if (!activeJobList.AllCheckersForUnreferencingNodeConnectors.ContainsKey(key))
                {
                    activeJobList.AllCheckersForUnreferencingNodeConnectors.Add(key, shadowJobList.AllCheckersForUnreferencingNodeConnectors[key]);
                }
            }
            foreach (string key in shadowJobList.TreeExternalCheckers.Keys)
            {
                if (!activeJobList.TreeExternalCheckers.ContainsKey(key))
                {
                    activeJobList.TreeExternalCheckers.Add(key, shadowJobList.AllCheckersForUnreferencingNodeConnectors[key]);
                }
            }
            foreach (SingleNode shadowNode in shadowJobList.TreeExternalSingleNodes)
            {
                bool found = false;
                foreach (SingleNode treeNode in activeJobList.TreeExternalSingleNodes)
                {
                    if (treeNode.Equals(shadowNode))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    activeJobList.TreeExternalSingleNodes.Add(shadowNode);
                }
            }
            foreach (string key in shadowJobList.TriggerRelevantEventCache)
            {
                if (!activeJobList.TriggerRelevantEventCache.Contains(key))
                {
                    activeJobList.TriggerRelevantEventCache.Add(key);
                }
            }
            foreach (string key in shadowJobList.LoggerRelevantEventCache)
            {
                if (!activeJobList.LoggerRelevantEventCache.Contains(key))
                {
                    activeJobList.LoggerRelevantEventCache.Add(key);
                }
            }
            foreach (string key in shadowJobList.WorkerRelevantEventCache)
            {
                if (!activeJobList.WorkerRelevantEventCache.Contains(key))
                {
                    activeJobList.WorkerRelevantEventCache.Add(key);
                }
            }
            foreach (string key in shadowJobList.JobsByName.Keys)
            {
                if (!activeJobList.JobsByName.ContainsKey(key))
                {
                    activeJobList.JobsByName.Add(key, shadowJobList.JobsByName[key]);
                }
            }
            foreach (string key in shadowJobList.NodesByName.Keys)
            {
                if (!activeJobList.NodesByName.ContainsKey(key))
                {
                    activeJobList.NodesByName.Add(key, shadowJobList.NodesByName[key]);
                }
            }
            foreach (string key in shadowJobList.TreeRootLastChanceNodesByName.Keys)
            {
                if (!activeJobList.TreeRootLastChanceNodesByName.ContainsKey(key))
                {
                    activeJobList.TreeRootLastChanceNodesByName.Add(key, shadowJobList.TreeRootLastChanceNodesByName[key]);
                }
            }
            foreach (string key in shadowJobList.NodesById.Keys)
            {
                if (!activeJobList.NodesById.ContainsKey(key))
                {
                    activeJobList.NodesById.Add(key, shadowJobList.NodesById[key]);
                }
            }
        }

        private static void RemoveOldJobListGlobals(JobList shadowJobList, JobList activeJobList)
        {
            InfoController.Say(String.Format($"#RELOAD# Transferring Tree Globals from ShadowTree to Tree."));

            List<string> keys;
            try
            {
                keys = new List<string>(activeJobList.Job.EventTriggers.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.Job.EventTriggers.ContainsKey(key))
                    {
                        activeJobList.Job.EventTriggers.Remove(key);
                    }
                    else
                    {
                        List<string> keyKeys = new List<string>(activeJobList.Job.EventTriggers[key].Keys);
                        foreach (string keyKey in keyKeys)
                        {
                            if (!shadowJobList.Job.EventTriggers[key].ContainsKey(keyKey))
                            {
                                activeJobList.Job.EventTriggers[key].Remove(keyKey);
                            }
                        }
                    }
                }

            }
            catch (Exception ex1)
            {
                throw;
            }

            try
            {
                keys = new List<string>(activeJobList.Job.WorkersDictionary.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.Job.WorkersDictionary.ContainsKey(key))
                    {
                        activeJobList.Job.WorkersDictionary.Remove(key);
                    }
                    else
                    {
                        List<string> keyKeys = new List<string>(activeJobList.Job.WorkersDictionary[key].Keys);
                        foreach (string keyKey in keyKeys)
                        {
                            if (!shadowJobList.Job.WorkersDictionary[key].ContainsKey(keyKey))
                            {
                                activeJobList.Job.WorkersDictionary[key].Remove(keyKey);
                            }
                            else
                            {
                                List<WorkerShell> combinedTreeWorkers = new List<WorkerShell>();
                                foreach (WorkerShell keyKeyTreeWorker in activeJobList.Job.WorkersDictionary[key][keyKey])
                                {
                                    string treeSlave = keyKeyTreeWorker.SlavePathName;
                                    bool found = false;
                                    foreach (WorkerShell keyKeyShadowWorker in shadowJobList.Job.WorkersDictionary[key][keyKey])
                                    {
                                        string shadowSlave = keyKeyShadowWorker.SlavePathName;
                                        if (treeSlave == shadowSlave)
                                        {
                                            found = true;
                                            break;
                                        }
                                    }
                                    if (found)
                                    {
                                        combinedTreeWorkers.Add(keyKeyTreeWorker);
                                    }
                                }
                                if (activeJobList.Job.WorkersDictionary[key][keyKey].Length > combinedTreeWorkers.Count)
                                {
                                    activeJobList.Job.WorkersDictionary[key][keyKey] = combinedTreeWorkers.ToArray();
                                }
                            }
                        }
                    }
                }

            }
            catch (Exception ex2)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.AllCheckersForUnreferencingNodeConnectors.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.AllCheckersForUnreferencingNodeConnectors.ContainsKey(key))
                    {
                        activeJobList.AllCheckersForUnreferencingNodeConnectors.Remove(key);
                    }
                }
            }
            catch (Exception ex3)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.TreeExternalCheckers.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.TreeExternalCheckers.ContainsKey(key))
                    {
                        activeJobList.TreeExternalCheckers.Remove(key);
                    }
                }
            }
            catch (Exception ex4)
            {
                throw;
            }
            try
            {
                List<SingleNode> remainingSingleNodes = new List<SingleNode>();
                foreach (SingleNode treeNode in activeJobList.TreeExternalSingleNodes)
                {
                    bool found = false;
                    foreach (SingleNode shadowNode in shadowJobList.TreeExternalSingleNodes)
                    {
                        if (treeNode.Equals(shadowNode))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found)
                    {
                        remainingSingleNodes.Add(treeNode);
                    }
                }
                if (activeJobList.TreeExternalSingleNodes.Count > remainingSingleNodes.Count)
                {
                    activeJobList.TreeExternalSingleNodes = remainingSingleNodes;
                }
            }
            catch (Exception ex5)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.TriggerRelevantEventCache);
                foreach (string key in keys)
                {
                    if (!shadowJobList.TriggerRelevantEventCache.Contains(key))
                    {
                        activeJobList.TriggerRelevantEventCache.Remove(key);
                    }
                }
            }
            catch (Exception ex6)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.LoggerRelevantEventCache);
                foreach (string key in keys)
                {
                    if (!shadowJobList.LoggerRelevantEventCache.Contains(key))
                    {
                        activeJobList.LoggerRelevantEventCache.Remove(key);
                    }
                }
            }
            catch (Exception ex7)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.WorkerRelevantEventCache);
                foreach (string key in keys)
                {
                    if (!shadowJobList.WorkerRelevantEventCache.Contains(key))
                    {
                        activeJobList.WorkerRelevantEventCache.Remove(key);
                    }
                }
            }
            catch (Exception ex8)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.JobsByName.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.JobsByName.ContainsKey(key))
                    {
                        activeJobList.JobsByName.Remove(key);
                    }
                }
            }
            catch (Exception ex9)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.NodesByName.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.NodesByName.ContainsKey(key))
                    {
                        activeJobList.NodesByName.Remove(key);
                    }
                }
            }
            catch (Exception ex10)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.TreeRootLastChanceNodesByName.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.TreeRootLastChanceNodesByName.ContainsKey(key))
                    {
                        activeJobList.TreeRootLastChanceNodesByName.Remove(key);
                    }
                }
            }
            catch (Exception ex11)
            {
                throw;
            }
            try
            {
                keys = new List<string>(activeJobList.NodesById.Keys);
                foreach (string key in keys)
                {
                    if (!shadowJobList.NodesById.ContainsKey(key))
                    {
                        activeJobList.NodesById.Remove(key);
                    }
                }
            }
            catch (Exception ex12)
            {
                throw;
            }
        }

        /// <summary>
        /// Fügt den Path der ExpandableNode als Key und die ExpandableNode als Value in ein als object übergebenes Dictionary ein.
        /// </summary>
        /// <param name="depth">Nullbasierter Zähler der Rekursionstiefe eines Knotens im LogicalTaskTree.</param>
        /// <param name="expandableNode">Basisklasse eines ViewModel-Knotens im LogicalTaskTree.</param>
        /// <param name="userObject">Ein beliebiges durchgeschliffenes UserObject (hier: Dictionary&lt;string, LogicalNodeViewModel&gt;).</param>
        /// <returns>Das bisher gefüllte Dictionary mit Path als Key und dem LogicalNodeViewModel als Value.</returns>
        internal static object IndexTreeElement(int depth, IExpandableNode expandableNode, object userObject)
        {
            Dictionary<string, LogicalNodeViewModel> vmFinder = (Dictionary<string, LogicalNodeViewModel>)userObject;
            if (expandableNode is LogicalNodeViewModel)
            {
                LogicalNodeViewModel logicalNodeViewModel = expandableNode as LogicalNodeViewModel;
                // string key = Regex.Replace(logicalNodeViewModel.Path, @"Internal_\d+", "Internal");
                string key = logicalNodeViewModel.Path;
                vmFinder.Add(key, logicalNodeViewModel);
            }
            return userObject;
        }

        /// <summary>
        /// Fügt tatsächlich referenzierte Objekte in eine als object übergebene List ein.
        /// </summary>
        /// <param name="depth">Nullbasierter Zähler der Rekursionstiefe eines Knotens im LogicalTaskTree.</param>
        /// <param name="expandableNode">Basisklasse eines ViewModel-Knotens im LogicalTaskTree.</param>
        /// <param name="userObject">Ein beliebiges durchgeschliffenes UserObject (hier: Dictionary&lt;string, LogicalNodeViewModel&gt;).</param>
        /// <returns>Das bisher gefüllte Dictionary mit Path als Key und dem LogicalNodeViewModel als Value.</returns>
        private static object CollectReferencedObjects(int depth, IExpandableNode expandableNode, object userObject)
        {
            List<object> allReferencedObjects = (List<object>)userObject;
            if (expandableNode is LogicalNodeViewModel)
            {
                LogicalNodeViewModel logicalNodeViewModel = expandableNode as LogicalNodeViewModel;
                LogicalNode logicalNode = logicalNodeViewModel.GetLogicalNode();

                // --- Trigger ---------------------------------------------------------------------------------
                if (logicalNode?.Trigger != null)
                {
                    if (!allReferencedObjects.Contains(logicalNode.Trigger))
                    {
                        allReferencedObjects.Add(logicalNode.Trigger);
                    }
                }
                if ((logicalNode as SingleNode)?.Checker != null)
                {
                    if ((logicalNode as SingleNode)?.Checker.CheckerTrigger != null)
                    {
                        if (!allReferencedObjects.Contains((logicalNode as SingleNode)?.Checker.CheckerTrigger))
                        {
                            allReferencedObjects.Add((logicalNode as SingleNode)?.Checker.CheckerTrigger);
                        }
                    }
                }
                if ((logicalNode as JobList)?.Job.JobTrigger != null)
                {
                    if (!allReferencedObjects.Contains((logicalNode as JobList)?.Job.JobTrigger))
                    {
                        allReferencedObjects.Add((logicalNode as JobList)?.Job.JobTrigger);
                    }
                }
                if ((logicalNode as JobList)?.Job.JobSnapshotTrigger != null)
                {
                    if (!allReferencedObjects.Contains((logicalNode as JobList)?.Job.JobSnapshotTrigger))
                    {
                        allReferencedObjects.Add((logicalNode as JobList)?.Job.JobSnapshotTrigger);
                    }
                }
                // --- Worker-Trigger -------------------------------------------------------------------------
                if ((logicalNode as JobList)?.Job.Workers != null)
                {
                    Workers workers = (logicalNode as JobList)?.Job.Workers;
                    List<string> keys = new List<string>(workers.Keys);
                    Dictionary<string, Dictionary<string, WorkerShell[]>>.ValueCollection values = workers.Values;
                    foreach (Dictionary<string, WorkerShell[]> element in values)
                    {
                        Dictionary<string, WorkerShell[]>.ValueCollection valuesValues = element.Values;
                        foreach (WorkerShell[] workerShellArray in valuesValues)
                        {
                            for (int i = 0; i < workerShellArray.Length; i++)
                            {
                                if (workerShellArray[i].Trigger != null)
                                {
                                    if (!allReferencedObjects.Contains(workerShellArray[i].Trigger))
                                    {
                                        allReferencedObjects.Add(workerShellArray[i].Trigger);
                                    }
                                }
                            }
                        }
                    }
                }

                // --- Logger ---------------------------------------------------------------------------------
                if (logicalNode?.Logger != null)
                {
                    if (!allReferencedObjects.Contains(logicalNode.Logger))
                    {
                        allReferencedObjects.Add(logicalNode.Logger);
                    }
                }
                if ((logicalNode as SingleNode)?.Checker != null)
                {
                    if ((logicalNode as SingleNode)?.Checker.CheckerLogger != null)
                    {
                        if (!allReferencedObjects.Contains((logicalNode as SingleNode)?.Checker.CheckerLogger))
                        {
                            allReferencedObjects.Add((logicalNode as SingleNode)?.Checker.CheckerLogger);
                        }
                    }
                }
                if ((logicalNode as JobList)?.Job.JobLogger != null)
                {
                    if (!allReferencedObjects.Contains((logicalNode as JobList)?.Job.JobLogger))
                    {
                        allReferencedObjects.Add((logicalNode as JobList)?.Job.JobLogger);
                    }
                }
            }
            return userObject;
        }

        /// <summary>
        /// Fügt Informationen über die übergebene ExpandableNode in eine als object übergebene Stringlist ein.
        /// </summary>
        /// <param name="depth">Nullbasierter Zähler der Rekursionstiefe eines Knotens im LogicalTaskTree.</param>
        /// <param name="expandableNode">Basisklasse eines ViewModel-Knotens im LogicalTaskTree.</param>
        /// <param name="userObject">Ein beliebiges durchgeschliffenes UserObject (hier: List&lt;string&gt;).</param>
        /// <returns>Die bisher gefüllte Stringlist mit Knoteninformationen.</returns>
        private static object ListTreeElement(int depth, IExpandableNode expandableNode, object userObject)
        {
            string depthString = depth > 0 ? new String(' ', depth * 4) : "";
            List<string> allTreeInfos = (List<string>)userObject;
            LogicalNodeViewModel logicalNodeViewModel = expandableNode as LogicalNodeViewModel;
            string viewModelToStringReducedSpaces = Regex.Replace(logicalNodeViewModel.ToString().Replace(Environment.NewLine, ", "), @"\s{2,}", " ");
            viewModelToStringReducedSpaces += logicalNodeViewModel.VisualTreeCacheBreaker;
            if (logicalNodeViewModel.Parent != null)
            {
                viewModelToStringReducedSpaces += Environment.NewLine + depthString
                    + "                          Parent: " + logicalNodeViewModel.Parent.TreeParams.Name + ": " + logicalNodeViewModel.Parent.Id;
            }
            else
            {
                viewModelToStringReducedSpaces += Environment.NewLine + depthString
                    + "                          Parent: NULL";
            }
            viewModelToStringReducedSpaces += GetViewModelChildrenString(logicalNodeViewModel);
            viewModelToStringReducedSpaces += Environment.NewLine + depthString
                    + "                          hooked to: " + logicalNodeViewModel.HookedTo;
            allTreeInfos.Add(depthString + logicalNodeViewModel.TreeParams.ToString() + ": " + viewModelToStringReducedSpaces);

            if (expandableNode is JobListViewModel)
            {
                LogicalTaskTreeManager.AddJobListGlobals(depthString + "    ", expandableNode as JobListViewModel, allTreeInfos);
            }

            LogicalTaskTreeManager.AddBusinessNodeDetails(depthString + "    ", expandableNode as LogicalNodeViewModel, allTreeInfos);

            return userObject;
        }

        private static string GetViewModelChildrenString(LogicalNodeViewModel parent)
        {
            string children = "";
            if (!(parent is SingleNodeViewModel))
            {
                children = " Children: ";
                string delimiter = "";
                foreach (LogicalNodeViewModel child in parent.Children)
                {
                    children += delimiter + child.TreeParams.Name + ": " + child.Id;
                    delimiter = ", ";
                }
            }
            return children;
        }

        private static void AddBusinessNodeDetails(string depthString, LogicalNodeViewModel logicalNodeViewModel, List<string> allTreeInfos)
        {
            LogicalNode blNode = logicalNodeViewModel.GetLogicalNode();
            if (blNode != null)
            {
                string motherInfo;
                if (blNode.Mother != null)
                {
                    motherInfo = "Mother: " + (blNode.Mother as LogicalNode).TreeParams.Name + ": " + (blNode.Mother as LogicalNode).IdInfo;
                }
                else
                {
                    motherInfo = "Mother: NULL";
                }
                string childrenInfo = "";
                string hookInfo = "";
                if (blNode is NodeParent)
                {
                    childrenInfo = GetNodeChildrenString(blNode as NodeParent);
                    hookInfo = Environment.NewLine + depthString
                        + "                          hooked to: " + (blNode as NodeParent).HookedTo;
                }
                string blNodeToString = depthString + blNode.TreeParams.ToString() + ": "
                    + blNode.ToString().Replace(Environment.NewLine, Environment.NewLine
                    + depthString).TrimEnd().TrimEnd(Environment.NewLine.ToCharArray())
                    + hookInfo;
                allTreeInfos.Add(blNodeToString + ", " + motherInfo + childrenInfo + Environment.NewLine);
            }
            else
            {
                allTreeInfos.Add(depthString + "NOTHING" + Environment.NewLine);
            }
        }

        private static string GetNodeChildrenString(NodeParent parent)
        {
            string children = " Children: ";
            string delimiter = "";
            foreach (LogicalNode child in parent.Children)
            {
                children += delimiter + child.TreeParams.Name + ": " + child.IdInfo;
                delimiter = ", ";
            }
            return children;
        }

        private static void AddJobListGlobals(string depthString, JobListViewModel rootJobListVM, List<string> allTreeInfos)
        {
            StringBuilder stringBuilder;
            JobList rootJobList = rootJobListVM.GetLogicalNode() as JobList;
            if (rootJobList != null)
            {
                stringBuilder = new StringBuilder(depthString + String.Format($"--- Globals von {rootJobList.NameId} ---"));
                List<string> keys;
                stringBuilder.Append(Environment.NewLine + depthString + "EventTriggers" + Environment.NewLine);
                keys = new List<string>(rootJobList.Job.EventTriggers.Keys);
                string delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key + ": ");
                    delimiter = " | ";
                    List<string> keyKeys = new List<string>(rootJobList.Job.EventTriggers[key].Keys);
                    string delimiter2 = "";
                    foreach (string keyKey in keyKeys)
                    {
                        stringBuilder.Append(delimiter2 + keyKey);
                        delimiter2 = ", ";
                    }
                }

                stringBuilder.Append(Environment.NewLine + depthString + "Workers" + Environment.NewLine);
                keys = new List<string>(rootJobList.Job.WorkersDictionary.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    // Event
                    stringBuilder.Append(delimiter + key + ": ");
                    delimiter = " | ";
                    List<string> keyKeys = new List<string>(rootJobList.Job.WorkersDictionary[key].Keys);
                    string delimiter2 = "";
                    foreach (string keyKey in keyKeys)
                    {
                        stringBuilder.Append(delimiter2 + keyKey);
                        delimiter2 = ", ";
                        // Knoten
                        List<WorkerShell> combinedTreeWorkers = new List<WorkerShell>();
                        string delimiter3 = ": ";
                        foreach (WorkerShell keyKeyTreeWorker in rootJobList.Job.WorkersDictionary[key][keyKey])
                        {
                            // Exe
                            stringBuilder.Append(delimiter3 + Path.GetFileName(keyKeyTreeWorker.SlavePathName));
                            delimiter3 = ", ";
                        }
                    }
                }

                stringBuilder.Append(Environment.NewLine + depthString + "AllCheckers" + Environment.NewLine);
                keys = new List<string>(rootJobList.AllCheckersForUnreferencingNodeConnectors.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "TreeExternalCheckers" + Environment.NewLine);
                keys = new List<string>(rootJobList.TreeExternalCheckers.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "TreeExternalSingleNodes" + Environment.NewLine);
                delimiter = depthString + "    ";
                foreach (SingleNode treeNode in rootJobList.TreeExternalSingleNodes)
                {
                    stringBuilder.Append(delimiter + treeNode.NameId);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "TriggerRelevantEventCache" + Environment.NewLine);
                keys = new List<string>(rootJobList.TriggerRelevantEventCache);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "LoggerRelevantEventCache" + Environment.NewLine);
                keys = new List<string>(rootJobList.LoggerRelevantEventCache);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "WorkerRelevantEventCache" + Environment.NewLine);
                keys = new List<string>(rootJobList.WorkerRelevantEventCache);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "JobsByName" + Environment.NewLine);
                keys = new List<string>(rootJobList.JobsByName.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "NodesByName" + Environment.NewLine);
                keys = new List<string>(rootJobList.NodesByName.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "TreeRootLastChanceNodesByName" + Environment.NewLine);
                keys = new List<string>(rootJobList.TreeRootLastChanceNodesByName.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.Append(Environment.NewLine + depthString + "NodesById" + Environment.NewLine);
                keys = new List<string>(rootJobList.NodesById.Keys);
                delimiter = depthString + "    ";
                foreach (string key in keys)
                {
                    stringBuilder.Append(delimiter + key);
                    delimiter = ", ";
                }

                stringBuilder.AppendLine(Environment.NewLine + depthString + $"----------------" + new String('-', rootJobList.NameId.Length) + "----");
            }
            else
            {
                stringBuilder = new StringBuilder(depthString + String.Format($"--- Globals von {rootJobListVM.Name} ---"));
                allTreeInfos.Add(depthString + "NOTHING" + Environment.NewLine);
                stringBuilder.AppendLine(Environment.NewLine + depthString + $"----------------" + new String('-', rootJobListVM.Name.Length) + "----");
            }
            allTreeInfos.Add(stringBuilder.ToString());
        }
    }
}