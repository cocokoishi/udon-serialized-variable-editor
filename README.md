
**serializedpublicvariables bytestrings editor for vrchat udonsharp**

A simple Unity tool to modify the `serializedPublicVariables` data in Udon behaviours.

**Dependencies:** Requires the **VRChat Worlds SDK** to function (Built and tested on SDK 3.10.1).

### Why use this?

If you look at an UdonBehaviour in Unity's default Debug inspector, you can see the `serializedPublicVariables` byte string. However, Unity doesnot show objects like **String** values stored inside a password lock. You know the data is there, but you can't read it.

This script parses that raw data, reveals the hidden info, and lets you modify it. It's super useful if you've forgotten the passcode to a keypad in an old World AssetBundle and need to recover it.

<img width="1231" height="655" alt="sk" src="https://github.com/user-attachments/assets/4932df92-894d-435c-ad85-b041d43ce97f" />

**Note on Udon Assets:**
This might also be able to modify non-public variables inside Udon Program Assets. However, since VRChat signs Udon scripts to prevent tampering, you'll probably need to do some extra work (ed25519) to make those changes stick.

### Usage

Just drop the script into your Unity project assets. You can access it via the top menu under `Tools`.

### Credits

Big thanks to **paran3xus** and their [udon-decompiler](https://github.com/ParaN3xus/udon-decompiler) project for the inspiration and technical groundwork.
