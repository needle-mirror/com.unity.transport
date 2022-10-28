---
id: logging
title: Integration with Unity.Logging
---

Unity Transport is integrated with Unity Logging (`com.unity.logging package`), an efficient alternative to the standard Unity logger. Normally, log messages would all go through `UnityEngine.Debug.Log` but when both packages are included in a project, Unity Transport will automatically use Unity.Logging with the default logger settings. 

Check the [Unity.Logging documentation site](https://docs.unity3d.com/Packages/com.unity.logging@latest) for more information on how to adjust specific log settings. 