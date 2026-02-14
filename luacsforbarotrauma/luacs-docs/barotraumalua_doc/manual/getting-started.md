# Getting started

If you want to learn how Lua works and the syntax, you can check these websites: [https://www.lua.org/manual/5.2/](https://www.lua.org/manual/5.2/) [https://www.tutorialspoint.com/lua/lua_overview.htm](https://www.tutorialspoint.com/lua/lua_overview.htm)

## How mods are executed
When the server finishes loading everything, Lua For Barotrauma starts up and reads the file `Lua/LuaSetup.lua` and executes it, this Lua script registers classes to be available on the Lua side, creates static references and puts them in the global space, and then goes on each mod and executes their Autorun if the content package is enabled, and after that, goes on each mod, and executes ForcedAutorun which executes even if the content package is disabled.

## Creating your first mod
When creating a new Lua mod, you will need to create a new folder on the **LocalMods** folder, then inside this folder you will need to create a folder called **Lua**, and then inside the Lua folder you create a folder called **Autorun**, inside this folder you can add your Lua scripts that will be executed automatically, it will look something like this `LocalMods/MyMod/Lua/Autorun/test.lua`, remember that your mod will only work if it's a valid xml mod that has a filelist.xml. If you use ForcedAurorun, the mod executes even if it's disabled.

Now you can open **test.lua** in your favorite text editor (<a href="https://code.visualstudio.com/" target="_blank">VSCode</a> with the <a href="https://marketplace.visualstudio.com/items?itemName=sumneko.lua" target="_blank">Lua Sumneko extension</a> recommended) and type in **print("Hello, world")**, now start the server by hosting a game or running the server executable manually, you will see in your console a text appear with the text that you entered in print, you can now type in the server console `reloadlua`, this will re-execute all the Lua scripts. You can also type in `lua yourscript` to run a short lua snippet, like this `lua print('Hello, world')`, remember to remove double quotes, because Barotrauma console automatically formats those.
**Note: When you host a server via the in game menus, you won't be able to see the first server debug prints, because the server will print those before your client joins the server.**

## Including other files

If you wish to separate your Lua scripts into multiple files, you can do it by either having multiple scripts in the Autorun folder, or having a single script that is responsible for executing the rest of your Lua scripts, for that you will need to get relative paths to your mod, here's an example of how to do it:

```
-- this variable will be accessible to any other script, so you can use it to get the mod's path.
MyModGlobal = {}

MyModGlobal.Path = ...

dofile(MyModGlobal.Path .. "/Lua/yourscript.lua")
```

Alternatively, you may use the `require` function to load Lua scripts by their module name (file name without `.lua` inside your mod's `Lua/` folder, subfolders are dot-separated). This is slightly different from `dofile`; `require` will return whatever the Lua script you are calling returns. Also, `require` will execute the Lua script only the first time, and will just return its original return value on subsequent calls.

```
local MyMod = {
    Path = ... -- Get path to this mod/content package, exact same as MyModGlobal.Path = ... above.
}

-- Equivalent to: dofile(MyMod.Path .. "/Lua/MyScript.lua")
require "MyScript"

-- Equivalent to: dofile(MyMod.Path .. "/Lua/Subfolder1/Subfolder2/Subfoler3/MyScript.lua")
require "Subfolder1.Subfolder2.Subfolder3.MyScript"
```

Note that `require` does not necessitate the use of `MyMod.Path = ...`, instead it looks for the first match in the `Lua/` folder of every enabled (including forced ran) Barotrauma mod. This means that you can execute the scripts of other Barotrauma mods. To ensure that you do not inadvertently execute a script from a different Barotrauma mod, you should use a unique subfolder name for your scripts.

```
-- Generic: Bad, likely to execute MyScript.lua from a different mod from ours.
-- AnyModPath/Lua/MyScript.lua
require "MyScript"

-- More unique: Good, less likely to execute MyScript from a different mod from ours.
-- AnyModPath/Lua/MyName/MyMod/MyScript.lua
require "MyName.MyMod.MyScript"
```

`require` will return whatever the script it loads returns.

```
-- Inside MyScript.lua:
local MyString = "Hello World!"

return MyString

-------------------------------

-- Inside another script:
local MyString = require "MyScript"

-- Will print "Hello World!"
print(MyString)
```

The main advantages of `require` over `dofile` are: 1. multiple scripts can try to load the same file without executing its contents multiple times but instead only once; and 2. scripts can load scripts from third-party mods without having to know their paths. It is an alternative over using global variables for sharing data between scripts; while a script needs to wait for a global variable to be initialised first, you can instead bundle the variable initalisation logic (which must only run once) with a script that also returns the variable (in a table) for other mods to `require` and use.

Find more information about `require` in the Programming in Lua book: http://www.lua.org/pil/8.1.html.

## Error handling

Sometimes you may expect an error to happen when you call specific functions in your script. Errors will stop the execution of your script unless they are handled correctly.

`pcall` (protected call) is a function that allows you to call another function in *protected mode*, which means that any errors that occur will be caught and a status code will be returned that your script can use to understand whether the function failed or succeeded, and the type of error that occured.

```
-- Require a third-party script, risky as it raises an error if the user does not have it installed.
local result = require "ThirdPartyAuthor.ThirdPartyMod.ThirdPartyScript"
-- Only prints if the above does not raise an error.
print(result)

-- Same as above, except any error is handled.
local ok, result = pcall(require, "ThirdPartyAuthor.ThirdPartyMod.ThirdPartyScript")
-- `ok` is true if no error, false if there is an error.
if ok then
    -- No error, print our result!
    print(result)
else
    -- There was an error, in this case `result` is a string containing the error code.
    print("Error when loading third-party script: ", result)
end
```

Read more about error handling in the Programming in Lua book: http://www.lua.org/pil/8.4.html. Confer also the Lua 5.2 reference manual: http://www.lua.org/manual/5.2/manual.html.

## Learning the libraries
In the sidebar of the documentation, you can see a tab named Code, in there you can check out all the functions and fields that each class has, each one of them has a box with a color on it, where <span class="realm server"></span> means Server-Side, <span class="realm client"></span> means Client-Side and <span class="realm shared"></span> means both Server-Side and Client-Side, by clicking on them you can learn more about them. Not everything is documented here, theres stuff missing that still needs to be added, if you want to find more in-depth functions and fields in the Barotrauma classes, you should check the Barotrauma source code.

See <a href="../lua-examples" target="_blank">Lua Examples</a>, <a href="../common-questions" target="_blank">Common Questions</a>