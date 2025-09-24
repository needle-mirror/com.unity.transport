# Integration with `Unity.Logging`

The Unity Transport package no longer has special support for the Unity Logging package (`com.unity.logging`). This package is being deprecated in Unity 6.3 and its use is no longer recommended.

Unity Transport now only logs through the default `Debug.Log` mechanism. By default the logs will still be handled by the Unity Logging package if installed, since its default configuration includes handling of all Unity logs. The only loss of functionality in this scenario is that logs emitted from Unity Transport will no longer be properly structured. Very few logs used this capability however, so this should not be noticeable.