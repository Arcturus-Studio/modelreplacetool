# modelreplacetool
Unity tool for quickly replacing rigged models.

### Getting Started:
* Copy repo contents into your Unity project's Assets directory (or any subdirectory).
* Open the Model Replace Tool from the Window menu.
* Assign your old hierarchy root (in the scene) to the Source field.
* Assign the baseline for your old hierarchy (scene object or asset) to the Source Baseline field
  * The baseline is the asset the old hierarchy was before you changed it at all. e.g. What is imported from a 3d modelling program.

Once the source and baseline have been assigned, a tree with all additions made to the hierarchy will appear.
* Assign a destination root object (also in the scene) to the root node of the additions tree.
* Assign targets in the additions tree for any additions you want copied over.
* Assigning targets to non-addition nodes will not make them copy over, but helps the tool auto-fill the targets for other additions.
* Press the "Copy to Targets" button.
#### Additionally...
* If any postprocessing methods are executed, they will be logged to the console.
* If there are any references in the dest hierarchy that need to be fixed, they will be logged to the console.
* You probably want to turn the destination hierarchy into a prefab now.

The status indicator lights in the additions tree have tooltips that may tell you how to fix any setup issues.

### Postprocessing
Tag functions with the OnModelReplaced attribute to have them executed by the Model Replacer after all additions are copied over.
Only components that were modified or added will have their OnModelReplaced functions called.
This lets you do custom processing whenever a model is replaced.
