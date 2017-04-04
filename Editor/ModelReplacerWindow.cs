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
        Dictionary<UnityEngine.Object, UnityEngine.Object> refFixupMap;

        delegate void ReferenceProcessor(Component comp, SerializedProperty prop, UnityEngine.Object propObjRef);

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
            GUI.enabled = TargetsValid && treeValid;
            if (GUILayout.Button("Copy to Targets"))
            {
                RemapAndInvokeRecursive(treeRoot);
                ProcessCrossReferencesInTree();
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
            refFixupMap = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            List<MethodDef> methodsToInvoke = new List<MethodDef>();
            Stack<ModelTreeNode> nodes = new Stack<ModelTreeNode>();
            nodes.Push(start);
            while (nodes.Count > 0)
            {
                ModelTreeNode node = nodes.Pop();
                Remap(node, methodsToInvoke, refFixupMap);
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
        //All performed remaps (modified and cloned components, duplicated gameobjects, and targeted non-additions) are added to the remaps dictionary
        void Remap(ModelTreeNode node, List<MethodDef> methodsToInvoke, Dictionary<UnityEngine.Object, UnityEngine.Object> remaps)
        {
            if (node.obj != null && node.remapTarget != null)
            {
                remaps[node.obj] = node.remapTarget;
            }
            if (!node.hasAdditions || !node.isAddition || node.remapTarget == null)
            {
                return;
            }
            if (node.comps != null)
            {
                //Copy components to target
                List<Component> copiedComponents = CopyComponents(node.comps, node.remapTarget, node.conflictResolutions, remaps);

                //Get on-replace functions
                foreach (Component copiedComp in copiedComponents)
                {
                    methodsToInvoke.AddRange(GetOnReplaceMethodDefs(copiedComp, node.obj));
                }
            }
            else
            {
                //Duplicate and parent dupe to target
                GameObject dupe = Instantiate(node.obj);
                dupe.name = node.obj.name;
                dupe.transform.SetParent(node.remapTarget.transform, false);
                //Collect remaps and OnModelReplaced funcs in dupe
                Stack<Transform> dupes = new Stack<Transform>();
                Stack<Transform> originals = new Stack<Transform>();
                dupes.Push(dupe.transform);
                originals.Push(node.obj.transform);
                while (dupes.Count > 0)
                {
                    var dupeTrans = dupes.Pop();
                    var origTrans = originals.Pop();
                    remaps[origTrans.gameObject] = dupeTrans.gameObject;
                    Component[] dupeComps = dupeTrans.GetComponents<Component>();
                    Component[] origComps = origTrans.GetComponents<Component>();
                    for(int i = 0; i < dupeComps.Length; i++)
                    {
                        methodsToInvoke.AddRange(GetOnReplaceMethodDefs(dupeComps[i], origTrans.gameObject));
                        remaps[dupeComps[i]] = origComps[i];
                        //Sanity check / error out if Unity scrambles the component order (which it shouldnt)
                        if(dupeComps[i].GetType() != origComps[i].GetType())
                        {
                            throw new Exception("Component type mismatch");
                        }
                    }
                    foreach(Transform dupeChild in dupeTrans)
                    {
                        dupes.Push(dupeChild);
                    }
                    foreach(Transform origChild in origTrans)
                    {
                        originals.Push(origChild);
                    }
                }
            }
        }

        //Returns methodDefs for methods with the OnModelReplaced attribute in the given component.
        //The oldObj field of the methodDef is filled with the passed oldObject param.
        List<MethodDef> GetOnReplaceMethodDefs(Component comp, GameObject oldObject)
        {
            List<MethodDef> methodDefs = new List<MethodDef>();
            foreach (MethodInfo method in comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                object[] attributes = method.GetCustomAttributes(typeof(OnModelReplacedAttribute), true);
                if (attributes.Length > 0)
                {
                    methodDefs.Add(new MethodDef(method, ((OnModelReplacedAttribute)attributes[0]).priority, comp, oldObject));
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
            invokeLog.AppendLine("Invoked " + methodsToInvoke.Count + " on-replace methods");
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
        List<Component> CopyComponents(List<Component> srcComps, GameObject dest, Dictionary<Type, CopyConflictMode> conflictResolutions, Dictionary<UnityEngine.Object, UnityEngine.Object> remaps)
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
                    remaps[comp] = target;
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

        //Finds all serialized references from DEST to SRC (and their descendents and components) and
        //tries to fix any cross-references to point to the correct object in DEST instead. Logs fixed and unfixed references.
        void ProcessCrossReferencesInTree()
        {
            UnityEngine.Object[] fromHierarchy = EditorUtility.CollectDeepHierarchy(new UnityEngine.Object[] { treeRoot.remapTarget });
            UnityEngine.Object[] toHierarchy = EditorUtility.CollectDeepHierarchy(new UnityEngine.Object[] { srcObj });
            var toHashSet = new HashSet<UnityEngine.Object>(toHierarchy); //dump into hashset for fast finds
            var compToNodeMap = new Dictionary<Component, ModelTreeNode>();
            treeRoot.FillComponentToNodeMap(compToNodeMap);
            StringBuilder fixedRefLog = new StringBuilder();
            StringBuilder unfixedRefLog = new StringBuilder();
            for (int i = 0; i < fromHierarchy.Length; i++)
            {
                if (fromHierarchy[i] is Component)
                {
                    ProcessReferencesInComponent(fromHierarchy[i] as Component, toHashSet, (comp, prop, objRef) => {
                        if (FixCrossReference(prop, refFixupMap, compToNodeMap))
                        {
                            fixedRefLog.AppendLine(comp.gameObject.name + " -> " + comp.GetType() + "." + prop.propertyPath);
                        }
                        else
                        {
                            unfixedRefLog.AppendLine(comp.gameObject.name + " -> " + comp.GetType() + "." + prop.propertyPath);
                        }
                        ;
                    });
                }
            }
            if(fixedRefLog.Length > 0)
            {
                fixedRefLog.Insert(0, "References to the source hierarchy discovered and fixed automatically:\n");
                Debug.Log(fixedRefLog.ToString());
            }
            if (unfixedRefLog.Length > 0)
            {
                unfixedRefLog.Insert(0, "References to the source hierarchy discovered and could not be fixed automatically:\n");
                Debug.LogWarning(unfixedRefLog.ToString());
            }                   
        }

        //Finds serializedproperties in the given component to any object in the given set of targets.
        //Performs the given action for each such found serialized property.
        //Note that the serialized property is an iterator and should not be retained as part of the action as it will be invalid later.
        void ProcessReferencesInComponent(Component comp, HashSet<UnityEngine.Object> targets, ReferenceProcessor action)
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
                        action(comp, serProp, objRef);
                    }
                }
            }
            while (serProp.Next(true));            
        }

        //Tries to automatically fix a property which holds an objectreference to the old hierarchy
        //Returns true if a fix was applied, false otherwise
        bool FixCrossReference(SerializedProperty objProperty, Dictionary<UnityEngine.Object, UnityEngine.Object> remaps, Dictionary<Component, ModelTreeNode> compToNode)
        {            
            //References to gameobjects we copied
            //References to components we copied            
            //References to gameobjects with remap targets, even if they weren't copied due to not being additions
            if (remaps.ContainsKey(objProperty.objectReferenceValue))
            {
                objProperty.objectReferenceValue = remaps[objProperty.objectReferenceValue];
                objProperty.serializedObject.ApplyModifiedPropertiesWithoutUndo();                
                return true;
            }
            //References to components that weren't copied, if they are on gameobjects with remap targets
            //AND the user didn't specify this was a component conflict where DEST should be preserved
            else if (objProperty.objectReferenceValue is Component)
            {
                var comp = objProperty.objectReferenceValue as Component;
                if(remaps.ContainsKey(comp.gameObject))
                {
                    Component[] srcComps = comp.gameObject.GetComponents(comp.GetType());
                    Component[] destComps = (remaps[comp.gameObject] as GameObject).GetComponents(comp.GetType());
                    for(int i = 0; i < srcComps.Length && i < destComps.Length; i++)
                    {
                        if(srcComps[i] == comp)
                        {
                            ModelTreeNode node = null;
                            compToNode.TryGetValue(comp, out node);
                            if (node == null 
                                || node.conflictResolutions == null 
                                || !node.conflictResolutions.ContainsKey(comp.GetType()) 
                                || node.conflictResolutions[comp.GetType()] != CopyConflictMode.KeepDest)
                            {                                    
                                objProperty.objectReferenceValue = destComps[i];
                                objProperty.serializedObject.ApplyModifiedPropertiesWithoutUndo();
                                return true;
                            }
                            return false; //conflict mode was "keep dest"
                        }
                    }               
                }
            }
            return false;            
        }
    }
}