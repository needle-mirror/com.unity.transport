# Integration with `Unity.Logging`

Unity Transport offers an optional integration with the Unity Logging package (`com.unity.logging package`). This package is a flexible alternative to the classic Unity logging mechanism and is particularly useful for production servers.

Normally, log messages would all go through `UnityEngine.Debug.Log`, but when the logging package is included in a project, Unity Transport will automatically use `Unity.Logging` with the default logger settings. 

Check the [logging package documentation site](https://docs.unity3d.com/Packages/com.unity.logging@latest) for more information on how to adjust specific log settings. 