﻿using System;

namespace MainServiceProvider
{
    /// <summary>
    /// When used with the SimpleIoc container, specifies which constructor
    /// should be used to instantiate when GetInstance is called.
    /// If there is only one constructor in the class, this attribute is
    /// not needed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class MainConstructorAttribute : Attribute
    {
    }
}