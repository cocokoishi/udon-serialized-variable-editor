
**serializedpublicvariables bytestrings editor for vrchat udonsharp**

A simple Unity tool to losslessly modify the `serializedPublicVariables` data in Udon behaviours.

**Dependencies:** Requires the **VRChat Worlds SDK** to function (Built and tested on SDK 3.10.1).

### Why use this?

While playing a world or *opening* a world assetbundle, if you look at an UdonBehaviour in Unity's default Debug inspector, you can see the `serializedPublicVariables` byte string. However, Unity doesnot show objects like **String** values stored inside a password lock. You know the data is there, but you can't read it.

This script parses that raw data, reveals the hidden info, and lets you modify it. It's super useful if you've forgotten the passcode to a keypad in an old World AssetBundle and need to recover it. 

<img width="904" height="734" alt="QQ_1769488906892" src="https://github.com/user-attachments/assets/cadb5058-baf4-4fba-b8b9-acc1167b1d1a" />


**Note on Udon Assets:**
This might also be able to modify non-public variables inside Udon Program Assets. However, since VRChat signs Udon scripts to prevent cheaters, you'll probably need to do some extra work (ed25519) to make those changes available for actual game client.

### Usage

Just drop the script into your Unity project assets. You can access it via the top menu under `Tools`. Then drag the gameobject on the hierarchy into the script and parser. Then you can losslessly change the variables and export new. Dont directly input base64 serializedpublicvariables, for at most time it doesnot work losslessly.

### Limits
Though we tested it in a lot of LLM-written sophisticated udon scripts and passed perfectly, it does not support some complicated edits. It passed all of my udongraph(human-written) worlds,so I think it can satisfy 99% needs.

### Credits

Big thanks to **paran3xus** and their [udon-decompiler](https://github.com/ParaN3xus/udon-decompiler) project for the inspiration and technical groundwork.  Thanks to **椰子梨梨花**'s videos demonstrating that worlds' public variables can be read and edited.
