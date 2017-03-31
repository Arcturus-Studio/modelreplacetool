using UnityEngine;
using System.Collections;
using System;

namespace ModelReplaceTool
{
    /* Tag functions with this attribute to have them executed by the ModelReplacerWindow after all additions are copied over.
     * Only components that are modified/added will have their OnModelReplaced functions called.
     * Tagged functions must be public instance functions or they will not be discovered.
     * Tagged functions can either have 0 parameters, or 1 GameObject parameter, in which case they are passed the gameobject the original
     * component was attached to.
     * Tagged functions are executed in order from lowest value priority to highest. Ties in priority are broken in undefined order.
     * */
    public class OnModelReplacedAttribute : Attribute
    {

        public int priority; //lower number = earlier execution

        public OnModelReplacedAttribute()
        {
            priority = 0;
        }

        public OnModelReplacedAttribute(int priority)
        {
            this.priority = priority;
        }
    }
}