# CyclesMod

This mod makes cycles independent of load times. That means you will be able to make even the most ridiculous and absurdly precise cycles no matter how good or bad your computer is. Note that this mod allows for some cycles which cannot otherwise be made, and it may make you too fast for some cycles; this can be fixed by changing the settings.

# How to Use

Normalize Cycles is the main setting of the mod. Turning this setting off means cycles will play out differently each time the room is reloaded. You should only turn this off to experiment with deliberately bad loads or to try and reproduce a specific PC setup.

By default, cycles will play under theoretically ideal conditions, as if loads were instant. This means objects start their cycles as late as possible. You can set a specific offset from the ideal time by editing the Extra Load Time setting. For example, setting Extra Load Time to 0.1s means cycles will start 0.1s earlier than the ideal time. If you find that having Normalize Loads enabled forces you to slow down to make cycles, you should increase this setting.

Force Clear Memory is meant for research purposes only. This introduces extra variance into loads that is likely to cause cycles to get off-sync. This is not the same as the cycle starting earlier: it means different objects in the room will individually start running at different times. Again, this for experimenting with bad loads.

Settings Cheat Sheet:

- Best loads: Normalize Loads = ON, Extra Load Time = 0.0, Force Clear Memory = OFF
- Good loads: Normalize Loads = ON, Extra Load Time > 0.0, Force Clear Memory = OFF
- Vanilla loads: Normalize Loads = OFF, Extra Load Time = 0.0, Force Clear Memory = OFF
- Bad loads: Normalize Loads = OFF, Extra Load Time > 0.0, Force Clear Memory = ON

# How Cycles Work

Silksong (and Hollow Knight) only keep the current room loaded; the rest of the map is always unloaded. Whenever you go through a screen transition, the game pauses to load the new room into the game and unload the old room. This is called a "load." The amount of time a load takes depends on many things, but in general, better PCs will finish loads faster. This is why Hollow Knight and Silksong speedruns use an autosplitter which pauses the timer during loads: otherwise, people with worse computers would unfairly be forced to get slower times with the same gameplay.

It is a common belief that longer loads will result in worse cycles, since the game will have been running for longer by the time the load ends and the player regains control. However, this is **not actually true** (at least not strictly). To show why, and to demonstrate what actually causes cycle variance, we need to look deeper into how a load is structured.

The game internally divides a load into several different sections, called phases. The following is a brief summary of the purpose of each phase. Phases which do not always run are italicized.

- *FetchBlocked*: On some platforms and for some transitions, the game will wait for the camera to fade to black before continuing. This phase is generally skipped.
- *ClearMemPreFetch*: If the game is running low on memory, the game will manually run garbage collection to free up space for the load. This phase runs infrequently and takes about 0.1s or less to complete.
- Fetch: The game loads all assets for the new scene from the game files into memory. This phase makes up the majority of the load and can take anywhere from 0.1s to 2 or more seconds depending on hardware speed and the complexity of objects in the new scene (e.g. towns will take longer than most other rooms).
- *ActivationBlocked*: The game waits for the camera to finish fading to black if it hasn't already, and it usually has by now.
-  Activation: The game instantiates all of the objects specified by the scene, which includes running `Awake()` for each of them. This takes about 0.05s.
-  *ClearMemPostActivation*: The game once again checks for low memory and frees up what it can if needed. This runs at about the same frequency as ClearMemPreFetch and takes 0.3-0.4s. This phase will always run if you enable Force Clear Memory in the mod settings.
-  GarbageCollect: This phase always runs unless ClearMemPostActivation was ran and does a faster garbage collect. It takes 0.1s or less.
-  StartCall: The game waits exactly one frame (or does it?). This lets the newly instantiated objects run their `Start()` methods, which could take up to 0.1s for large scenes.
-  *LoadBoss*: Some scenes contain an additional "boss scene" which now needs to be loaded separately, but only if the boss is still alive. This scene is usually much smaller than the main scene, so this phase takes about 0.5 to 1 seconds.

While every phase is affected to some degree by computer speed, the main variance in load times comes from the Fetch phase. The length of this phase is mainly determined by how fast the computer's hard disk is. However, this phase also has nothing to do with cycles. The objects in the new scene only exist after the Activation phase, so any phase before that cannot possibly affect their behavior.

The real culprit is, surprisingly, StartCall. But how does that make sense? You would expect variance in StartCall to offset cycles by at most a frame, which is 0.016s at 60fps, and even less at higher frame rates. To be fair, that frame is a lag frame because of all the work the game has to do on it, but that should still cause an error of at most 0.1s. And indeed, you are correct. The problem is not what StartCall is doing, the problem is what it's *not* doing... because it happened in an earlier phase by mistake.

# The Problem

Because of an oversight by the game developers, it is very likely that objects' `Start()` methods will run before StartCall has even begun. This is because `Start()` is called on the first frame an object exists, which will always be the first frame after the Activation phase. That frame will occur during either the ClearMemPostActivation or GarbageCollect phases, both of which are doing some heavy-handed cleaning-out of memory. This causes multiple small lag spikes over the course of the frame, on top of the lag spikes caused by the `Start()` methods themselves, some of which are quite slow.

Even so, none of this should matter because everything is still starting on the same frame. Since time only advances in discrete steps between frames, everything should still start at the same in-game time and be fine. But of course it isn't that simple. For some mysterious reason, Unity will forcibly interrupt a frame to start a new one if there is enough lag on the first frame, probably to "catch up" to the frame rate. And the in-game time is different during these two frames, since time runs at normal speed throughout the entire load (except for activation, I think). Since the frames are lag frames, this time difference can be severe; this is especially true if ClearMemPostActivation is running. I suspect the time can be off by as much as 0.5s, which is enough time to move several times the player's width horizontally in a sprint. Needless to say, that is a serious difference.

There is one more problem that puts the cherry on top of this entire mess. The StartCall phase waits one frame, during which any remaining `Start()` methods are allowed to run. But under very poor conditions, this is not what happens. What actually happens is only *most* of the `Start()` methods run during that frame. I believe this has to do with the order Unity visits objects during a frame. The object running the code for the load is somewhere in the middle of this order, so only some objects will have been updated by the time it gets woken up after the one-frame wait. Of course, that shouldn't matter because the objects would just be updated on the frame before, but Unity is weird. Since StartCall is normally the last phase, the game "finishes" the load with only some objects started. That then sets into motion a whole new series of events that lag the game even more. If StartCall waits two frames instead of one frame, this problem goes away.

These issues are what explain why some objects in the room can start their cycles off-sync from one another. The more severe problem, the fact that cycles start earlier or later depending on loads, is simply because time is still running normally during all of this. So depending on how much lag you get from these few frames, you may enter a room at about the same time as the cycles, or you may walk in only to find the cycles have taken off without you. The total length of the load has very little to do with this short section of the process, but it is true that less powerful PCs are likely to take longer here, and thus get worse cycles.

# How the Mod Fixes It

The fix is simply to pause the game time during the load, and to increase the StartCall wait to two frames instead of one. Of course, since literally all of the above happens in a single method (thank you Team Cherry), and that method happens to be an iterator block, we have to do some pretty messed up things to modify it in the ways we want. I wouldn't recommend reading the code for this mod unless you're very brave ;)
