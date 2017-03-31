using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace ModelReplaceTool
{
    public class ModelTreeNode
    {
        public GameObject obj;
        public List<Component> comps;
        public bool hasAdditions = true;
        public bool isAddition;
        public bool expanded = true; //for UI
        public List<ModelTreeNode> children = new List<ModelTreeNode>();
        public GameObject remapTarget;
        public bool remapTargetIsGuess = true;
        public Dictionary<Type, CopyConflictMode> conflictResolutions;
        public HashSet<Type> conflicts;

        public ModelTreeNode(GameObject obj, bool isAddition)
        {
            this.obj = obj;
            this.isAddition = isAddition;
        }

        public ModelTreeNode(GameObject obj, List<Component> comps)
        {
            this.obj = obj;
            this.comps = comps;
            isAddition = true;
        }

        public bool UpdateConflicts()
        {
            bool conflictsChanged = false;
            conflicts = new HashSet<Type>();
            if (comps != null && remapTarget != null)
            {
                foreach (Component comp in comps)
                {
                    if (remapTarget.GetComponent(comp.GetType()))
                    {
                        conflicts.Add(comp.GetType());
                    }
                }
            }

            if (conflictResolutions == null)
            {
                conflictResolutions = new Dictionary<Type, CopyConflictMode>();
            }
            foreach (Type addedType in conflicts)
            {
                if (!conflictResolutions.ContainsKey(addedType))
                {
                    conflictResolutions[addedType] = ModelReplacerWindow.DefaultConflictResolution;
                    conflictsChanged = true;
                }
            }
            var toRemove = conflictResolutions.Where(kvp => !conflicts.Contains(kvp.Key)).Select(kvp => kvp.Key).ToArray();
            foreach (Type removedType in toRemove)
            {
                conflictResolutions.Remove(removedType);
                conflictsChanged = true;
            }
            return conflictsChanged;
        }
    }
}