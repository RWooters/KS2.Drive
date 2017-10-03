﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KS2Drive.FS
{
    public class CacheManager
    {
        public EventHandler CacheRefreshed;

        private static object CacheLock = new object();
        public Dictionary<String, FileNode> FileNodeCache = new Dictionary<string, FileNode>();

        public FileNode GetFileNode(String FileOrFolderLocalPath)
        {
            lock (CacheLock)
            {
                return GetFileNodeNoLock(FileOrFolderLocalPath);
            }
        }

        public FileNode GetFileNodeNoLock(String FileOrFolderLocalPath)
        {
            if (!FileNodeCache.ContainsKey(FileOrFolderLocalPath)) return null;
            else return FileNodeCache[FileOrFolderLocalPath];
        }

        public void AddFileNode(FileNode node)
        {
            lock (CacheLock)
            {
                AddFileNodeNoLock(node);
            }
        }

        public void AddFileNodeNoLock(FileNode node)
        {
            FileNodeCache.Add(node.LocalPath, node);
            CacheRefreshed?.Invoke(this, null);
        }

        /// <summary>
        /// Renomme une clé du dictionnaire
        /// </summary>
        public void RenameFileNodeKey(String PreviousKey, String NewKey)
        {
            lock (CacheLock)
            {
                FileNodeCache.RenameKey(PreviousKey, NewKey);
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void RenameFolderSubElements(String OldFolderName, String NewFolderName)
        {
            lock (CacheLock)
            {
                foreach (var FolderSubElement in FileNodeCache.Where(x => x.Key.StartsWith(OldFolderName + "\\")).ToList())
                {
                    String OldKeyName = FolderSubElement.Key;
                    FolderSubElement.Value.LocalPath = NewFolderName + FolderSubElement.Value.LocalPath.Substring(OldFolderName.Length);
                    FolderSubElement.Value.RepositoryPath = FileNode.ConvertLocalPathToRepositoryPath(FolderSubElement.Value.LocalPath);
                    String NewKeyName = FolderSubElement.Value.LocalPath;

                    FileNodeCache.RenameKey(OldKeyName, NewKeyName);

                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void DeleteFileNode(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                if ((node.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory) != 0)
                {
                    foreach (var s in FileNodeCache.Where(x => x.Key.StartsWith(node.LocalPath + "\\")).ToList())
                    {
                        FileNodeCache.Remove(s.Key);
                    }
                }
                CacheRefreshed?.Invoke(this, null);
            }
        }

        public void InvalidateFileNode(FileNode node)
        {
            lock (CacheLock)
            {
                FileNodeCache.Remove(node.LocalPath);
                String ParentFolderPath = node.LocalPath.Substring(0, node.LocalPath.LastIndexOf(@"\"));
                FileNodeCache[ParentFolderPath].IsParsed = false;
            }
        }

        /// <summary>
        /// Return folder content in the form of a list fo FileNodes
        /// If the folder as not already been parsed, we parse it from the server
        /// If the folder has been parsed, we serve content from the cache and update the cache from the server in a background task. So that we have a refreshed view for next call
        /// </summary>
        public (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) GetFolderContent(String FolderName, String Marker)
        {
            bool RunRefreshTask = false;
            List<Tuple<String, FileNode>> ReturnList = null;

            lock (CacheLock)
            {
                if (!FileNodeCache.ContainsKey(FolderName)) return (false, null, "Unknown folder");

                var CFN = FileNodeCache[FolderName];

                if (!CFN.IsParsed)
                {
                    var Result = InternalGetFolderContent(CFN, Marker);
                    if (!Result.Success) return Result;

                    //Mise en cache du contenu du répertoire
                    foreach (var Node in Result.Content)
                    {
                        if (Node.Item1 == "." || Node.Item1 == "..") continue;
                        this.AddFileNodeNoLock(Node.Item2);
                    }

                    ReturnList = Result.Content;
                }
                else
                {
                    String FolderNameForSearch = FolderName;
                    if (FolderNameForSearch != "\\") FolderNameForSearch += "\\";
                    ReturnList = new List<Tuple<String, FileNode>>();
                    //TODO : Add . && .. from cache
                    ReturnList.AddRange(FileNodeCache.Where(x => x.Key != FolderName && x.Key.StartsWith($"{FolderNameForSearch}") && x.Key.LastIndexOf('\\').Equals(FolderNameForSearch.Length - 1)).Select(x => new Tuple<String, FileNode>(x.Value.Name, x.Value)));
                    if ((DateTime.Now - FileNodeCache[FolderName].LastRefresh).TotalSeconds > 5) RunRefreshTask = true;
                }


                if (!String.IsNullOrEmpty(Marker)) //Dealing with potential marker
                {
                    ReturnList.RemoveAll(x => String.Compare(x.Item1,Marker, StringComparison.OrdinalIgnoreCase) < 1);
                    /*
                    var WantedTuple = ChildrenFileNames.FirstOrDefault(x => x.Item1.Equals(Marker));
                    var WantedTupleIndex = ChildrenFileNames.IndexOf(WantedTuple);
                    if (WantedTupleIndex + 1 < ChildrenFileNames.Count)
                    {
                        ChildrenFileNames = ChildrenFileNames.GetRange(WantedTupleIndex + 1, ChildrenFileNames.Count - 1 - WantedTupleIndex);
                    }
                    else
                    {
                        ChildrenFileNames.Clear();
                    }*/
                }

            }

            if (RunRefreshTask) Task.Run(() => InternalRefreshFolderCacheContent(FileNodeCache[FolderName]));
            return (true, ReturnList, null);
        }

        public void Clear()
        {
            FileNodeCache.Clear();
            FileNodeCache = null;
        }

        public void Lock()
        {
            Monitor.Enter(CacheLock);
        }

        public void Unlock()
        {
            Monitor.Exit(CacheLock);
        }

        private void InternalRefreshFolderCacheContent(FileNode CFN)
        {
            var Result = InternalGetFolderContent(CFN, null);
            if (!Result.Success) return;

            lock (CacheLock)
            {
                //Remove folder content
                foreach (var s in FileNodeCache.Where(x => x.Key.StartsWith(CFN.LocalPath + "\\")).ToList())
                {
                    FileNodeCache.Remove(s.Key);
                }

                //Refresh from server result
                foreach (var Node in Result.Content)
                {
                    if (Node.Item1 == "." || Node.Item1 == "..") continue;
                    this.AddFileNodeNoLock(Node.Item2);
                }

                CFN.LastRefresh = DateTime.Now;
            }
        }

        private (bool Success, List<Tuple<String, FileNode>> Content, String ErrorMessage) InternalGetFolderContent(FileNode CFN, String Marker)
        {
            var Proxy = new WebDavClient2();
            List<Tuple<String, FileNode>> ChildrenFileNames = new List<Tuple<String, FileNode>>();

            if (!FileNode.IsRepositoryRootPath(CFN.RepositoryPath))
            {
                //if this is not the root directory add the dot entries
                if (Marker == null) ChildrenFileNames.Add(new Tuple<String, FileNode>(".", CFN));

                if (null == Marker || "." == Marker)
                {
                    String ParentPath = FileNode.ConvertRepositoryPathToLocalPath(FileNode.GetRepositoryParentPath(CFN.RepositoryPath));
                    if (ParentPath != null)
                    {
                        //RepositoryElement ParentElement;
                        try
                        {
                            var ParentElement = Proxy.GetRepositoryElement(ParentPath);
                            if (ParentElement != null) ChildrenFileNames.Add(new Tuple<String, FileNode>("..", new FileNode(ParentElement)));
                        }
                        catch { }
                    }
                }
            }

            IEnumerable<WebDAVClient.Model.Item> ItemsInFolder;

            try
            {
                //LogTrace("Read directory list start");
                ItemsInFolder = Proxy.List(CFN.RepositoryPath).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }

            foreach (var Child in ItemsInFolder)
            {
                var Element = new FileNode(Child);
                if (Element.RepositoryPath.Equals(CFN.RepositoryPath)) continue; //Bypass l'entrée correspondant à l'élément appelant
                ChildrenFileNames.Add(new Tuple<string, FileNode>(Element.Name, Element));
            }

            CFN.IsParsed = true;
            CFN.LastRefresh = DateTime.Now;

            return (true, ChildrenFileNames, null);
        }
    }
}
