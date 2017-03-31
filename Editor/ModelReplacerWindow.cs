using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using System.Linq;

namespace ModelReplaceTool
{
    public enum CopyConflictMode
    {
        KeepBoth,    //Add clones from SRC, leave DEST copies unmodified
        KeepSrc,     //Destroy DEST copies, then clone from SRC                
        KeepDest,    //Do nothing to DEST
        ModifyDest,  //Overwrite values in DEST copies with SRC values. If less copies in DEST than SRC, appends the extras. If less in SRC than DEST, the extras are left unmodified.
    }

    public class ModelReplacerWindow : EditorWindow
    {
        struct MethodDef
        {
            public MethodInfo method;
            public int priority;
            public Component component;
            public GameObject oldObj;

            public MethodDef(MethodInfo method, int priority, Component component, GameObject oldObj)
            {
                this.method = method;
                this.priority = priority;
                this.component = component;
                this.oldObj = oldObj;
            }
        }

        [MenuItem("Window/Model Replacer")]
        public static void Open()
        {
            var window = GetWindow<ModelReplacerWindow>(false, "Model Replacer");
            window.Show();
        }

        public static CopyConflictMode DefaultConflictResolution = CopyConflictMode.ModifyDest;

        GameObject srcObj;
        GameObject srcModel;
        ModelTreeNode treeRoot;
        Vector2 scrollPos;

        bool TargetsValid
        {
            get { return srcObj != null && srcModel != null; }
        }

        private void OnGUI()
        {
            scrollPos = GUILayout.BeginScrollView(scrollPos);
            bool objsChanged = DrawTargetSelection();
            if (objsChanged)
            {
                treeRoot = null;
            }
            if (treeRoot == null && TargetsValid)
            {
                treeRoot = FindAdditions();
            }
            bool treeValid = false;
            if (TargetsValid)
            {
                CheckConflictsRecursive(treeRoot);
                GUILayout.Label("Assign targets in new hierarchy to attach additions to.", EditorStyles.wordWrappedLabel);
                treeValid = DrawAdditionTree(treeRoot, 0);
                if (!treeRoot.hasAdditions)
                {
                    EditorGUILayout.HelpBox("Found no additions to Source. Nothing to do here!", MessageType.Info);
                    treeValid = false;
                }
            }

            //Execute button
            GUI.enabled = TargetsValid && treeRoot.remapTarget != null;
            if (GUILayout.Button("Copy to Targets"))
            {
                RemapAndInvokeRecursive(treeRoot);
                FindCrossReferencesInTree(treeRoot.remapTarget, srcObj);
            }
            GUI.enabled = true;
            GUILayout.EndScrollView();
        }

        //Draws source and source baseline selection fields.
        //Returns true if the field values changed, false otherwise.
        private bool DrawTargetSelection()
        {
            //Target selection
            GUILayout.Label("Assign an object and a baseline to find additions compared to the baseline.", EditorStyles.wordWrappedLabel);
            EditorGUI.BeginChangeCheck();
            srcObj = EditorGUILayout.ObjectField("Source", srcObj, typeof(GameObject), true) as GameObject;
            srcModel = EditorGUILayout.ObjectField("Source Baseline", srcModel, typeof(GameObject), true) as GameObject;
            bool objsChanged = EditorGUI.EndChangeCheck();

            //Report problems with object selection
            if (srcObj == null)
            {
                GUILayout.Label("Assign a source and destination object");
            }
            else if (srcModel == null)
            {
                GUILayout.Label("Assign the source model from the project");
            }
            return objsChanged;
        }

        //Recursively draws the hierarchy rooted at the given node, starting at the given indent level.
        //Returns true if the tree properties are valid for doing the copy operation, false otherwise.
        bool DrawAdditionTree(ModelTreeNode node, int indentLevel)
        {
            const int spacePerIndent = 12; //EditorGUI.indentLevel messes with field width, do indenting ourselves
            if (!node.hasAdditions)
            {
                //Don't draw branches with no additions in them
                return true; 
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(spacePerIndent * indentLevel);
            bool selfValid = true;
            //Components
            if (node.comps != null)
            {
                GUILayout.Space(spacePerIndent); //indent an extra level
                GUILayout.BeginVertical();
                selfValid = DrawAddition(node);
                DrawConflictResolution(node);
                GUILayout.EndVertical();

            }
            //Object
            else if (node.obj != null)
            {
                if (node.children.Count > 0)
                {
                    node.expanded = EditorGUILayout.Foldout(node.expanded, node.obj.name);
                    selfValid = DrawRemapTargetField(node);
                    GUI.color = Color.white;
                }
                else
                {
                    selfValid = DrawAddition(node);
                }
            }
            GUILayout.EndHorizontal();
            //Recurse on children
            bool childrenValid = true;
            if (node.expanded)
            {
                for (int i = 0; i < node.children.Count; i++)
                {
                    childrenValid &= DrawAdditionTree(node.children[i], indentLevel + 1);
                }
            }
            return selfValid && childrenValid;           
        }

        //Draws a node which is an addition.
        //Returns true if the node properties are valid for doing the copy operation, false otherwise.
        private bool DrawAddition(ModelTreeNode node)
        {
            string label;
            Texture icon;
            //Determine label and icon
            if (node.comps == null)
            {
                label = node.obj.name;
                icon = AssetPreview.GetMiniTypeThumbnail(typeof(GameObject));
            }
            else
            {
                StringBuilder compString = new StringBuilder();
                for (int i = 0; i < node.comps.Count; i++)
                {
                    compString.Append(node.comps[i].GetType().Name);
                    if (i < node.comps.Count - 1)
                    {
                        compString.Append(", ");
                    }
                }
                label = compString.ToString(); //could cache this, maybe
                icon = AssetPreview.GetMiniTypeThumbnail(typeof(Component));
            }
            //Draw
            GUILayout.BeginHorizontal();
            GUILayout.BeginHorizontal(GUI.skin.box);
            EditorGUILayout.LabelField(new GUIContent(icon), GUILayout.Height(16), GUILayout.Width(16));
            EditorGUILayout.LabelField(label, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            bool valid = DrawRemapTargetField(node);
            GUILayout.EndHorizontal();
            return valid;
        }

        //Returns true if the remap target for the given node is valid for doing the copy operation, false otherwise.
        bool DrawRemapTargetField(ModelTreeNode node)
        {
            //Status lamp and target validation
            bool valid = true;
            string statusTooltip;
            if (node.isAddition)
            {
                if (node.remapTarget == null)
                {                      
                    GUI.color = Color.yellow;
                    statusTooltip = "No target specified: Addition will not be copied";
                }
                else if(treeRoot.remapTarget == null)
                {
                    GUI.color = Color.yellow;
                    statusTooltip = "The root node must have a remap target specified to validate this target";
                    valid = false;
                }
                else if(!node.remapTarget.transform.IsChildOf(treeRoot.remapTarget.transform))
                {
                    GUI.color = Color.red;
                    statusTooltip = "Target must be a descendent of the root node's target";
                    valid = false;
                }
                else
                {
                    GUI.color = Color.green;
                    statusTooltip = "Addition will be copied and attached to specified target";
                }
            }
            else
            {
                if (node == treeRoot && node.remapTarget == null)
                {
                    GUI.color = Color.red;
                    statusTooltip = "The root node must have a remap target specified.";
                    valid = false;
                }
                else
                {
                    GUI.color = Color.grey;
                    statusTooltip = "This is not an addition and will not be copied. Set this target to give hints for auto-filling other targets.";
                }
            }
            GUILayout.Label(new GUIContent("", statusTooltip), EditorGUIUtility.GetBuiltinSkin(EditorSkin.Inspector).GetStyle("radio"), GUILayout.Width(16)); //EditorSkin.Inspector = "Light"/Personal skin
            GUI.color = Color.white;
            //Object field
            EditorStyles.objectField.fontStyle = node.remapTargetIsGuess ? FontStyle.Normal : FontStyle.Bold;
            EditorGUI.BeginChangeCheck();
            Rect controlRect = EditorGUILayout.GetControlRect(GUILayout.Width(200));
            node.remapTarget = EditorGUI.ObjectField(controlRect, node.remapTarget, typeof(GameObject), true) as GameObject;
            EditorStyles.objectField.fontStyle = FontStyle.Normal;
            //Context click for "clear" menu
            if (Event.current.type == EventType.ContextClick && controlRect.Contains(Event.current.mousePosition))
            {
                GenericMenu menu = new GenericMenu();
                menu.AddItem(new GUIContent("Clear"), false, ResetRemapTarget, node);
                menu.ShowAsContext();
                Event.current.Use();
            }
            //Re-guess tree from this point if remap target changed
            if (EditorGUI.EndChangeCheck())
            {
                node.remapTargetIsGuess = false;
                GuessChildRemapTargetsRecursive(node);
            }
            return valid;
        }

        //Context menu callback
        void ResetRemapTarget(System.Object obj)
        {
            var node = obj as ModelTreeNode;
            node.remapTargetIsGuess = true;
            node.remapTarget = null;
            GuessChildRemapTargetsRecursive(treeRoot);
        }

        //Draws component conflict resolution UI for all conflicting types in given node
        private void DrawConflictResolution(ModelTreeNode node)
        {
            if (node.conflicts != null && node.conflicts.Count > 0)
            {
                GUILayout.Label("Some components exist on Source and Dest. Choose how to resolve conflicts.", EditorStyles.wordWrappedLabel);
                foreach (Type conflictType in node.conflicts)
                {
                    if (!node.conflictResolutions.ContainsKey(conflictType))
                    {
                        node.conflictResolutions[conflictType] = DefaultConflictResolution;
                    }
                    node.conflictResolutions[conflictType] = (CopyConflictMode)EditorGUILayout.EnumPopup(conflictType.Name, node.conflictResolutions[conflictType], GUILayout.MaxWidth(320));
                }
            }
        }

        //Checks for conflicts in the tree rooted at the given node
        private void CheckConflictsRecursive(ModelTreeNode node)
        {
            if (node.UpdateConflicts())
            {
                Repaint();
            }
            for (int i = 0; i < node.children.Count; i++)
            {
                CheckConflictsRecursive(node.children[i]);
            }
        }

        //Performs copy of all nodes in tree rooted at given node to their remapTargets, and invokes the OnModelReplaced function in any copied components.
        //(not *actually* recursive)
        void RemapAndInvokeRecursive(ModelTreeNode start)
        {
            List<MethodDef> methodsToInvoke = new List<MethodDef>();
            Stack<ModelTreeNode> nodes = new Stack<ModelTreeNode>();
            nodes.Push(start);
            while (nodes.Count > 0)
            {
                ModelTreeNode node = nodes.Pop();
                Remap(node, methodsToInvoke);
                foreach (ModelTreeNode c in node.children)
                {
                    if (node.hasAdditions)
                    {
                        nodes.Push(c);
                    }
                }
            }
            InvokeAll(methodsToInvoke);
        }

        //Copies the given node and attaches it to its remap target.
        //MethodsToInvoke is populated with MethodDefs for any methods in copied components with the OnModelReplaced attribute.
        void Remap(ModelTreeNode node, List<MethodDef> methodsToInvoke)
        {
            if (!node.hasAdditions || !node.isAddition || node.remapTarget == null)
            {
                return;
            }
            if (node.comps != null)
            {
                //Copy components to target
                List<Component> copiedComponents = CopyComponents(node.comps, node.remapTarget, node.conflictResolutions);

                //Get on-replace functions
                methodsToInvoke.AddRange(GetOnReplaceMethodDefs(copiedComponents, node.obj));
            }
            else
            {
                //Duplicate and parent dupe to target
                GameObject dupe = Instantiate(node.obj);
                dupe.name = node.obj.name;
                dupe.transform.SetParent(node.remapTarget.transform, false);
                //Collect OnModelReplaced funcs in dupe
                Stack<Transform> dupes = new Stack<Transform>();
                Stack<Transform> originals = new Stack<Transform>();
                dupes.Push(dupe.transform);
                originals.Push(node.obj.transform);
                while (dupes.Count > 0)
                {
                    Transform dupeTrans = dupes.Pop();
                    Transform origTrans = originals.Pop();
                    methodsToInvoke.AddRange(GetOnReplaceMethodDefs(dupeTrans.GetComponents<Component>(), origTrans.gameObject));
                    foreach (Transform t in dupeTrans)
                    {
                        dupes.Push(t);
                    }
                    foreach (Transform t in origTrans)
                    {
                        originals.Push(t);
                    }
                }
            }
        }

        //Returns methodDefs for methods with the OnModelReplaced attribute in the given list of components.
        //The components are assumed to be on the same gameobject as each other.
        //The oldObj field of the methodDef is filled with the passed oldObject param.
        List<MethodDef> GetOnReplaceMethodDefs(IList<Component> comps, GameObject oldObject)
        {
            List<MethodDef> methodDefs = new List<MethodDef>();
            foreach (var comp in comps)
            {
                foreach (MethodInfo method in comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    object[] attributes = method.GetCustomAttributes(typeof(OnModelReplacedAttribute), true);
                    if (attributes.Length > 0)
                    {
                        methodDefs.Add(new MethodDef(method, ((OnModelReplacedAttribute)attributes[0]).priority, comp, oldObject));
                    }
                }
            }
            return methodDefs;
        }

        //Invokes all the methoddefs in the given list, sorted by priority.
        //If any of the invokes fail (throw an exception), the remainder are uninvoked.
        void InvokeAll(List<MethodDef> methodsToInvoke)
        {
            methodsToInvoke.Sort((x, y) => x.priority.CompareTo(y.priority));

            //Invoke on-replace functions
            StringBuilder invokeLog = new StringBuilder();
            invokeLog.AppendLine("Invoking " + methodsToInvoke.Count + " on-replace methods...");
            object[] oldObjParam = new object[1];
            foreach (var def in methodsToInvoke)
            {
                var methodParams = def.method.GetParameters();
                if (methodParams.Length == 0)
                {
                    if (!LoggedInvoke(def, null, invokeLog))
                    {
                        break;
                    }
                }
                else
                {
                    oldObjParam[0] = def.oldObj;
                    if (!LoggedInvoke(def, oldObjParam, invokeLog))
                    {
                        break;
                    }
                }
            }
            Debug.Log(invokeLog.ToString());
        }

        //Invokes the given methodDef and writes the method's name and containing type to invokeLog.
        //Returns true if the method was successfully invoked, false otherwise.        
        private bool LoggedInvoke(MethodDef def, object[] invokeParams, StringBuilder invokeLog)
        {
            //Note that a separate exception will get thrown and logged outside this try/catch block if the invoke fails.
            try
            {
                def.method.Invoke(def.component, invokeParams);
                invokeLog.AppendLine(def.component.GetType() + "." + def.method.Name);
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
                invokeLog.AppendLine("ERR: Exception invoking " + def.component.GetType() + "." + def.method.Name);
                return false;
            }
            return true;
        }

        //Copies the components in srcComps to dest. Conflicts (where dest already contains components of the same type)
        //are handled using the conflictResolutions dictionary.
        //Returns a list containing the new/modified components.
        List<Component> CopyComponents(List<Component> srcComps, GameObject dest, Dictionary<Type, CopyConflictMode> conflictResolutions)
        {
            List<Component> copiedComponents = new List<Component>();
            Dictionary<Type, Component[]> modifyComps = new Dictionary<Type, Component[]>();
            Dictionary<Type, int> modifyCount = new Dictionary<Type, int>();

            foreach (var comp in srcComps)
            {
                Type compType = comp.GetType();
                CopyConflictMode copyMode;
                if (!conflictResolutions.TryGetValue(compType, out copyMode))
                {
                    copyMode = CopyConflictMode.KeepBoth; //If no overriding mode specified, just add from SRC
                }

                UnityEditorInternal.ComponentUtility.CopyComponent(comp);
                Component target = null;
                switch (copyMode)
                {
                    case CopyConflictMode.KeepBoth:
                        target = dest.AddComponent(compType);
                        break;
                    case CopyConflictMode.KeepDest:
                        //Do nothing, dest is not modified
                        break;
                    case CopyConflictMode.KeepSrc:
                        //Destroy dest components
                        Component[] destComps = dest.GetComponents(compType);
                        for (int i = 0; i < destComps.Length; i++)
                        {
                            GameObject.DestroyImmediate(destComps[i]);
                        }
                        //Dont destroy dest components again next time
                        conflictResolutions[compType] = CopyConflictMode.KeepBoth;
                        target = dest.AddComponent(compType);
                        break;
                    case CopyConflictMode.ModifyDest:
                        if (!modifyComps.ContainsKey(compType))
                        {
                            modifyComps[compType] = dest.GetComponents(compType);
                            modifyCount[compType] = 0;
                        }
                        if (modifyCount[compType] < modifyComps[compType].Length)
                        {
                            target = modifyComps[compType][modifyCount[compType]];
                        }
                        else
                        {
                            target = dest.AddComponent(compType);
                        }
                        modifyCount[compType] += 1;
                        break;
                    default:
                        throw new Exception("Unknown CopyConflictMode: " + copyMode);
                }
                if (target != null)
                {
                    UnityEditorInternal.ComponentUtility.PasteComponentValues(target);
                    copiedComponents.Add(target);
                }
            }
            return copiedComponents;
        }

        /* Build list of additions between srcObj and srcModel
        * Preconditions:
        * 1. No renaming or reordering of descendents from SRCMODEL, only insertions and deletions
        * 2. If any object in SRC/SRCMODEL has siblings with the same name, and any such objects were removed or inserted, they appear(ed) after all their duplicates
        */
        private ModelTreeNode FindAdditions()
        {
            //Spawn srcModel into scene if it isn't already a scene object
            GameObject srcModelInstance;
            bool destroyAfter = false;
            if (AssetDatabase.Contains(srcModel))
            {
                destroyAfter = true;
                srcModelInstance = PrefabUtility.InstantiatePrefab(srcModel) as GameObject;
            }
            else
            {
                srcModelInstance = srcModel;
            }

            var root = FindAdditionsRecursive(srcModelInstance, srcObj);

            //Clean up
            if (destroyAfter)
            {
                DestroyImmediate(srcModelInstance);
            }

            return root;
        }

        private ModelTreeNode FindAdditionsRecursive(GameObject original, GameObject modified)
        {
            bool hasAdditions = false;
            var node = new ModelTreeNode(modified, false);
            //Check components        
            var modifiedComponents = modified.GetComponents<Component>();
            HashSet<Type> addedTypes = new HashSet<Type>();
            List<Component> addedComponents = new List<Component>();
            for (int i = 0; i < modifiedComponents.Length; i++)
            {
                Type compType = modifiedComponents[i].GetType();
                //Skip if already checked this type
                if (addedTypes.Contains(compType))
                {
                    continue;
                }
                //Add all extra components in modified to additions
                addedTypes.Add(compType);
                var modifiedOfType = modified.GetComponents(compType);
                var originalOfType = original.GetComponents(compType);
                for (int j = originalOfType.Length; j < modifiedOfType.Length; j++)
                {
                    addedComponents.Add(modifiedOfType[j]);
                    hasAdditions = true;
                }
            }
            if (addedComponents.Count > 0)
            {
                hasAdditions = true;
                node.children.Add(new ModelTreeNode(modified, addedComponents));
            }

            //Check child gameobjects
            int origChildIndex = 0;
            int modChildIndex = 0;
            while (modChildIndex < modified.transform.childCount)
            {
                if (origChildIndex >= original.transform.childCount)
                {
                    //Ran out of objs in original to match against: All remaining objs in modified are additions
                    for (int i = modChildIndex; i < modified.transform.childCount; i++)
                    {
                        hasAdditions = true;
                        node.children.Add(new ModelTreeNode(modified.transform.GetChild(i).gameObject, true));
                    }
                    break;
                }
                else if (original.transform.GetChild(origChildIndex).name == modified.transform.GetChild(modChildIndex).name)
                {
                    //Matched object: Check the subtree for changes
                    var childNode = FindAdditionsRecursive(original.transform.GetChild(origChildIndex).gameObject, modified.transform.GetChild(modChildIndex).gameObject);
                    hasAdditions |= childNode.hasAdditions;
                    node.children.Add(childNode);
                    origChildIndex++;
                    modChildIndex++;
                }
                else
                {
                    //Mismatched object: obj is either an addition to modified or was deleted from original                
                    //If it's an addition, we'll find the to the original later on in modified's child list. 
                    int nextMatchIndex = -1;
                    //Scan ahead in modified for match
                    for (int i = modChildIndex + 1; i < modified.transform.childCount; i++)
                    {
                        if (original.transform.GetChild(origChildIndex).name == modified.transform.GetChild(i).name)
                        {
                            nextMatchIndex = i;
                            break;
                        }
                    }
                    //Match found: Everything we skipped over in modified while scanning is an addition
                    if (nextMatchIndex >= 0)
                    {
                        for (int i = modChildIndex; i < nextMatchIndex; i++)
                        {
                            node.children.Add(new ModelTreeNode(modified.transform.GetChild(i).gameObject, true));
                            hasAdditions = true;
                        }
                        modChildIndex = nextMatchIndex; //will process as a match next loop
                    }
                    //No match found: An object was deleted from original
                    else
                    {
                        origChildIndex++;
                    }
                }
            }
            node.hasAdditions = hasAdditions;
            return node;
        }

        private void GuessChildRemapTargetsRecursive(ModelTreeNode node)
        {
            for (int nodeChildIndex = 0; nodeChildIndex < node.children.Count; nodeChildIndex++)
            {
                ModelTreeNode child = node.children[nodeChildIndex];
                if (child.remapTargetIsGuess)
                {
                    if (child.isAddition)
                    {
                        child.remapTarget = node.remapTarget;
                        continue; //additions never have children to guess remap targets for
                    }
                    else
                    {
                        child.remapTarget = null;
                        //Scan for obj under remap target with same name as child obj
                        if (node.remapTarget != null)
                        {
                            for (int targetChildIndex = 0; targetChildIndex < node.remapTarget.transform.childCount; targetChildIndex++)
                            {
                                if (child.obj.name == node.remapTarget.transform.GetChild(targetChildIndex).name)
                                {
                                    child.remapTarget = node.remapTarget.transform.GetChild(targetChildIndex).gameObject;
                                    break;
                                }
                            }
                        }

                    }
                }
                GuessChildRemapTargetsRecursive(child);
            }
        }

        //Finds serialized references in fromRoot (and its descendents and components) to toRoot (and its descendents and components)
        //Currently just logs everything it finds.
        void FindCrossReferencesInTree(GameObject fromRoot, GameObject toRoot)
        {
            UnityEngine.Object[] fromHierarchy = EditorUtility.CollectDeepHierarchy(new UnityEngine.Object[] { fromRoot });
            UnityEngine.Object[] toHierarchy = EditorUtility.CollectDeepHierarchy(new UnityEngine.Object[] { toRoot });
            var toHashSet = new HashSet<UnityEngine.Object>(toHierarchy); //dump into hashset for fast finds
            StringBuilder refLog = new StringBuilder();
            for (int i = 0; i < fromHierarchy.Length; i++)
            {
                if (fromHierarchy[i] is Component)
                {
                    FindReferences(fromHierarchy[i] as Component, toHashSet, (comp, objRef, prop) => { refLog.AppendLine(comp.gameObject.name + " -> " + comp.GetType() + "." + prop.propertyPath); });
                }
            }
            if (refLog.Length > 0)
            {
                refLog.Insert(0, "The following references to the source hierarchy were found after copy and could not be automatically fixed:\n");
                Debug.LogWarning(refLog.ToString());
            }
        }

        //Finds serializedproperties in the given component to any object in the given set of targets.
        //Performs the given action for each such found serialized property.
        void FindReferences(Component comp, HashSet<UnityEngine.Object> targets, Action<Component, UnityEngine.Object, SerializedProperty> action)
        {
            SerializedObject serObj = new SerializedObject(comp);
            SerializedProperty serProp = serObj.GetIterator();
            do
            {
                if (serProp.propertyType == SerializedPropertyType.ObjectReference)
                {
                    UnityEngine.Object objRef = serProp.objectReferenceValue;
                    if (objRef != null && targets.Contains(objRef))
                    {
                        action(comp, objRef, serProp);
                    }
                }
            }
            while (serProp.Next(true));
        }
    }
}