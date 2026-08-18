ANDROID SIGNING KEY - DO NOT DELETE
===================================

File:  signing-key-DO-NOT-DELETE.jks

What it is for
--------------
Every Android app must be signed. Android then accepts an update only if
the new version is signed with the SAME key.

If this file is lost
--------------------
The app on the phone can no longer be updated. It has to be uninstalled and
installed again, which also loses its settings and granted permissions.
Nothing worse happens, but it is a nuisance.

Why it lives here and not with the toolchain
--------------------------------------------
The build tools sit in `_android-build` (~700 MB), a folder meant to be
deleted once it is no longer needed. The key does not belong there - it
belongs to the project.

Password
--------
The password is NOT in this file, nor in the build script. It sits next to
the key in  signing-key-password.txt , which is listed in .gitignore and
never reaches the repository. The build script reads it from there; instead
of the file you can also set the DDL_KEYSTORE_PASSWORD environment
variable.

Without that file the app on the phone CANNOT be updated - back it up
together with the key.

Key replaced on 18 August 2026
------------------------------
The key was replaced because the original one carried the old name
`CN=Da Dynamic Lock`, from before the project was renamed.

  new  CN=Da BT Dynamic Lock, O=David, C=CZ
       SHA-256 fingerprint
       6618ce94260f1b3a0944d4d59f594ae27729bbf00256edcddb5c3ad36bb1c7ae
  old  CN=Da Dynamic Lock  - kept in
       signing-key-OLD-CN-Da-Dynamic-Lock.jks

CONSEQUENCE: an app signed with the new key CANNOT be installed over the old
one. The old app has to be uninstalled from the phone first.

The old key is not deleted: should an update ever be needed for a phone that
still runs the old version, it can be signed with it.

Backups
-------
The key belongs in your backup together with the password file. If the
project moves, both files must move with it - they are not in the
repository.
