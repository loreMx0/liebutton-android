using System;
using System.Collections;
using BepInEx;
using InControl;
using UnityEngine;

namespace LieButtonAndroid
{
    // Android port of the LieButton idea: toggle Hornet lying down / waking up.
    // Differences from the PC mod:
    //  - trigger can be a multi-finger tap (no keyboard on a phone)
    //  - config is read in Awake() inside try/catch (not in the constructor)
    //  - everything (Awake, Update, the coroutine) is wrapped so failures are logged
    //  - key is parsed once, hero/animation controller are cached (no per-frame GetComponent)
    //  - uses BepInEx Logger instead of UnityEngine.Debug
    // Change the GUID/name below to your own.
    [BepInPlugin("com.yourname.liebuttonandroid", "LieButton Android", "0.1.0")]
    public class LieButtonAndroidPlugin : BaseUnityPlugin
    {
        // Animation names used by the original mod
        private const string AnimKneel = "Collect Normal 1";
        private const string AnimFall = "Kneel To Prostrate";
        private const string AnimLie = "Prostrate";
        private const string AnimWake = "Prostrate Rise";

        // Only start lying down while Hornet is in one of these (idle-like) animations
        private static readonly string[] IdleAnims =
        {
            "Idle", "Idle Hurt", "LookDown", "LookDown Windy", "Hurt Look Up", "Hurt Look Up Windy",
            "Hurt Look Down", "Hurt Look Down Windy", "Idle Hurt Windy", "LookDown Updraft", "LookUp",
            "LookUp Updraft", "Look Up Half End", "LookDown Slight End", "LookUp Slight End", "Idle Rest",
            "Land", "Look Down Half End", "LookUp Windy", "Hurt Look Down Windy End", "HardLand",
            "Hurt Look Up Windy End", "Hurt Look Down End", "Hurt Look Up End", "Rage Idle", "Rage Idle End",
            "LookUpEnd Windy", "LookUpEnd", "LookUpEnd Updraft", "LookDownEnd", "LookDownEnd Windy",
            "LookDownEnd Updraft", "Idle Updraft", "Dash Down Land", "Idle Windy"
        };

        private KeyCode key = KeyCode.L;
        private bool keyValid = true;
        private bool wakeOnAttackOrJump;
        private int touchFingers = 3;

        private HeroController hero;
        private HeroAnimationController heroAnim;
        private bool isRunning;
        private bool togglePressed;      // computed once per frame in Update()
        private int lastTouchCount;
        private bool updateErrorLogged;

        private void Awake()
        {
            try
            {
                string keyName = "L";
                string wake = "false";
                string fingers = "3";

                try
                {
                    keyName = Config.Bind("General", "lieButton", keyName,
                        "KeyCode name that toggles lying down (e.g. L, JoystickButton2). None disables it.").Value;
                    wake = Config.Bind("General", "wakeOnAttackOrJump", wake,
                        "Wake up upon pressing Attack or Jump? true/false.").Value;
                    fingers = Config.Bind("General", "touchFingers", fingers,
                        "Number of fingers touching the screen at once that toggles lying down. 0 disables.").Value;
                }
                catch (Exception ex)
                {
                    Logger.LogError("Config failed, using defaults: " + ex);
                }

                keyValid = Enum.TryParse<KeyCode>(keyName, false, out key);
                if (!keyValid)
                {
                    key = KeyCode.None;
                    Logger.LogWarning("Can not parse key=" + keyName);
                }

                wakeOnAttackOrJump = wake != null && wake.Trim().ToLowerInvariant() == "true";

                if (!int.TryParse(fingers, out touchFingers))
                    touchFingers = 3;

                Logger.LogInfo("Awake. key=" + key + " touchFingers=" + touchFingers +
                               " wakeOnAttackOrJump=" + wakeOnAttackOrJump);
            }
            catch (Exception ex)
            {
                Logger.LogError("Awake failed: " + ex);
            }
        }

        private void Update()
        {
            try
            {
                togglePressed = PollToggle();

                HeroController h = HeroController.instance;
                if (h == null) return;

                if (h != hero)
                {
                    hero = h;
                    heroAnim = h.GetComponent<HeroAnimationController>();
                }

                if (!togglePressed || isRunning || heroAnim == null) return;
                if (h.controlReqlinquished || h.cState.transitioning) return;
                if (!IsIdleLike(heroAnim)) return;

                isRunning = true;
                StartCoroutine(LieDownSafe());
            }
            catch (Exception ex)
            {
                if (!updateErrorLogged)
                {
                    updateErrorLogged = true;
                    Logger.LogError("Update failed (logged once): " + ex);
                }
            }
        }

        // True on the frame the trigger happens: keyboard/controller KeyCode, or N fingers newly down.
        private bool PollToggle()
        {
            bool pressed = keyValid && key != KeyCode.None && Input.GetKeyDown(key);

            if (touchFingers > 0)
            {
                int count = Input.touchCount;
                if (count >= touchFingers && lastTouchCount < touchFingers)
                    pressed = true;
                lastTouchCount = count;
            }

            return pressed;
        }

        private static bool IsIdleLike(HeroAnimationController anim)
        {
            for (int i = 0; i < IdleAnims.Length; i++)
            {
                if (anim.animator.IsPlaying(IdleAnims[i]))
                    return true;
            }
            return false;
        }

        private bool WakeRequested()
        {
            if (togglePressed) return true;
            if (!wakeOnAttackOrJump) return false;

            HeroActions actions = ManagerSingleton<InputHandler>.Instance.inputActions;
            return ((OneAxisInputControl)actions.Jump).WasPressed ||
                   ((OneAxisInputControl)actions.Attack).WasPressed;
        }

        // Coroutines can't have try/catch around a yield, so drive the real coroutine by hand
        // and log anything it throws (this bridge swallows exceptions silently).
        private IEnumerator LieDownSafe()
        {
            IEnumerator inner = LieDown();
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) break;
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    Logger.LogError("LieDown failed: " + ex);
                    break;
                }
                yield return current;
            }
            isRunning = false;
        }

        private IEnumerator LieDown()
        {
            HeroController h = hero;
            HeroAnimationController anim = heroAnim;

            h.IgnoreInput();
            h.StopAnimationControl();
            h.controlReqlinquished = true;

            try
            {
                yield return PlayAndWait(anim, AnimKneel);
                yield return PlayAndWait(anim, AnimFall);

                anim.PlayClipForced(AnimLie);
                yield return null;

                while (true)
                {
                    if (h == null || anim == null) yield break;   // hero was destroyed
                    if (anim.animator.IsPlaying("Stun")) yield break;   // got hit: just give control back

                    if (WakeRequested())
                    {
                        yield return PlayAndWait(anim, AnimWake);
                        yield break;
                    }
                    yield return null;
                }
            }
            finally
            {
                if (h != null)
                {
                    h.AcceptInput();
                    h.controlReqlinquished = false;
                    h.StartAnimationControl();
                }
            }
        }

        private static IEnumerator PlayAndWait(HeroAnimationController anim, string clipName)
        {
            float remaining = anim.GetClipDuration(clipName);
            anim.PlayClipForced(clipName);
            var clip = anim.animator.CurrentClip;

            while (anim != null && anim.animator.IsPlaying(clip) && remaining > 0f)
            {
                remaining -= Time.deltaTime;
                yield return null;
            }
        }
    }
}
