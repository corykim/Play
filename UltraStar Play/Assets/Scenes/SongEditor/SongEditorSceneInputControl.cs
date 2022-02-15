﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniInject;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UniRx;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using PrimeInputActions;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

#pragma warning disable CS0649

public class SongEditorSceneInputControl : MonoBehaviour, INeedInjection
{
    private const float PinchGestureMagnitudeThresholdInPixels = 100f;
    
    public static readonly int cancelCopyPriority = 20;
    public static readonly int cancelNoteSelectionPriority = 10;
    
    [InjectedInInspector]
    public GameObject inputActionInfoOverlay;
    
    [Inject(SearchMethod = SearchMethods.FindObjectOfType)]
    private SongEditorSceneControl songEditorSceneControl;

    [Inject]
    private NoteAreaControl noteAreaControl;

    [Inject]
    private SongEditorSelectionControl selectionControl;

    [Inject]
    private EditorNoteDisplayer editorNoteDisplayer;

    [Inject]
    private SongEditorHistoryManager historyManager;

    [Inject]
    private Settings settings;

    [Inject]
    private SongMeta songMeta;

    [Inject]
    private DeleteNotesAction deleteNotesAction;

    [Inject]
    private SongAudioPlayer songAudioPlayer;

    [Inject]
    private EventSystem eventSystem;

    [Inject]
    private ToggleNoteTypeAction toggleNoteTypeAction;

    [Inject]
    private MoveNotesAction moveNotesAction;

    [Inject]
    private ExtendNotesAction extendNotesAction;

    private bool inputFieldHasFocusOld;

    private Vector2[] zoomStartTouchPositions;
    private Vector2 zoomStartTouchDistancePerDimension;
    
    private void Start()
    {
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Zoom Horizontal", "Ctrl+Mouse Wheel | 2 Finger Pinch-gesture"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Zoom Vertical", "Ctrl+Shift+Mouse Wheel | 2 Finger Pinch-gesture"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Scroll Horizontal", "Mouse Wheel | Arrow Keys | 2 Finger Drag | Middle Mouse Button+Drag"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Scroll Vertical", "Shift+Mouse Wheel | Shift+Arrow Keys | 2 Finger Drag | Middle Mouse Button+Drag"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Move Note Horizontal", "Shift+Arrow Keys | 1 (Numpad) | 3 (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Move Note Vertical", "Shift+Arrow Keys | Minus (Numpad) | Plus (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Move Note Vertical One Octave", "Ctrl+Shift+Arrow Keys"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Move Left Side Of Note", "Ctrl+Arrow Keys | Divide (Numpad) | Multiply (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Move Right side Of Note", "Alt+Arrow Keys | 7 (Numpad) | 8 (Numpad) | 9 (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Select Next Note", "6 (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Select Previous Note", "4 (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Play Selected Notes", "5 (Numpad)"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Toggle Play Pause", "Double Click"));
        UltraStarPlayInputManager.AdditionalInputActionInfos.Add(new InputActionInfo("Play MIDI Sound Of Note", "Ctrl+Click Note"));
        
        // Show / hide help
        InputManager.GetInputAction(R.InputActions.usplay_toggleHelp).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => inputActionInfoOverlay.SetActive(!inputActionInfoOverlay.activeSelf));
        
        // Jump to start / end of song
        InputManager.GetInputAction(R.InputActions.songEditor_jumpToStartOfSong).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => songAudioPlayer.PositionInSongInMillis = 0);
        InputManager.GetInputAction(R.InputActions.songEditor_jumpToEndOfSong).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => songAudioPlayer.PositionInSongInMillis = songAudioPlayer.DurationOfSongInMillis - 1);
        
        // Play / pause
        InputManager.GetInputAction(R.InputActions.songEditor_togglePlayPause).PerformedAsObservable()
            .Where(_ => !InputUtils.IsKeyboardControlPressed())
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => songEditorSceneControl.ToggleAudioPlayPause());

        // Play only the selected notes
        InputManager.GetInputAction(R.InputActions.songEditor_playSelectedNotes).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => PlayAudioInRangeOfNotes(selectionControl.GetSelectedNotes()));
        
        // Stop playback or return to last scene
        InputManager.GetInputAction(R.InputActions.usplay_back).PerformedAsObservable()
            .Subscribe(OnBack);
        
        // Select all notes
        InputManager.GetInputAction(R.InputActions.songEditor_selectAll).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => selectionControl.SelectAll());
        
        // Select next / previous note
        InputManager.GetInputAction(R.InputActions.songEditor_selectNextNote).PerformedAsObservable()
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => selectionControl.SelectNextNote());
        InputManager.GetInputAction(R.InputActions.songEditor_selectPreviousNote).PerformedAsObservable()
            .Subscribe(_ => selectionControl.SelectPreviousNote());
        
        // Deselect notes
        InputManager.GetInputAction(R.InputActions.usplay_back).PerformedAsObservable(cancelNoteSelectionPriority)
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed() && !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => selectionControl.HasSelectedNotes())
            .Subscribe(_ =>
            {
                selectionControl.ClearSelection();
                InputManager.GetInputAction(R.InputActions.usplay_back).CancelNotifyForThisFrame();
            });
        
        // Delete notes
        InputManager.GetInputAction(R.InputActions.songEditor_delete).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => deleteNotesAction.ExecuteAndNotify(selectionControl.GetSelectedNotes()));
        
        // Undo
        InputManager.GetInputAction(R.InputActions.songEditor_undo).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => historyManager.Undo());
        
        // Redo
        InputManager.GetInputAction(R.InputActions.songEditor_redo).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => historyManager.Redo());
        
        // Save
        InputManager.GetInputAction(R.InputActions.songEditor_save).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => songEditorSceneControl.SaveSong());
        
        // Start editing of lyrics
        InputManager.GetInputAction(R.InputActions.songEditor_editLyrics).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(_ => songEditorSceneControl.StartEditingNoteText());
        
        // Change position in song
        InputManager.GetInputAction(R.InputActions.ui_navigate).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(OnNavigate);
        
        // Make golden / freestyle / normal
        InputManager.GetInputAction(R.InputActions.songEditor_toggleNoteTypeGolden).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => toggleNoteTypeAction.ExecuteAndNotify(selectionControl.GetSelectedNotes(), ENoteType.Golden));
        
        InputManager.GetInputAction(R.InputActions.songEditor_toggleNoteTypeFreestyle).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => toggleNoteTypeAction.ExecuteAndNotify(selectionControl.GetSelectedNotes(), ENoteType.Freestyle));
        
        InputManager.GetInputAction(R.InputActions.songEditor_toggleNoteTypeNormal).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => toggleNoteTypeAction.ExecuteAndNotify(selectionControl.GetSelectedNotes(), ENoteType.Normal));
        
        InputManager.GetInputAction(R.InputActions.songEditor_toggleNoteTypeRap).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => toggleNoteTypeAction.ExecuteAndNotify(selectionControl.GetSelectedNotes(), ENoteType.Rap));
        
        InputManager.GetInputAction(R.InputActions.songEditor_toggleNoteTypeRapGolden).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.AnyKeyboardModifierPressed())
            .Subscribe(_ => toggleNoteTypeAction.ExecuteAndNotify(selectionControl.GetSelectedNotes(), ENoteType.RapGolden));
        
        // Zoom and scroll with mouse wheel
        InputManager.GetInputAction(R.InputActions.ui_scrollWheel).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(OnScrollWheel);
        
        // Zoom horizontal with shortcuts
        InputManager.GetInputAction(R.InputActions.songEditor_zoomInHorizontal).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.IsKeyboardShiftPressed())
            .Subscribe(context =>
            {
                noteAreaControl.ZoomHorizontal(1);
            });
        InputManager.GetInputAction(R.InputActions.songEditor_zoomOutHorizontal).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => !InputUtils.IsKeyboardShiftPressed())
            .Subscribe(context =>
            {
                noteAreaControl.ZoomHorizontal(-1);
            });

        // Zoom vertical with shortcuts
        InputManager.GetInputAction(R.InputActions.songEditor_zoomInVertical).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Where(_ => InputManager.GetInputAction(R.InputActions.songEditor_zoomOutVertical).ReadValue<float>() == 0)
            .Subscribe(context =>
            {
                noteAreaControl.ZoomVertical(1);
            });
        InputManager.GetInputAction(R.InputActions.songEditor_zoomOutVertical).PerformedAsObservable()
            .Where(_ => !GameObjectUtils.InputFieldHasFocus(eventSystem))
            .Subscribe(context => noteAreaControl.ZoomVertical(-1));
    }

    private void OnBack(InputAction.CallbackContext context)
    {
        if (songAudioPlayer.IsPlaying)
        {
            songAudioPlayer.PauseAudio();
        }
        else if (GameObjectUtils.InputFieldHasFocus(eventSystem))
        {
            // Deselect TextArea
            eventSystem.SetSelectedGameObject(null);
        }
        else if (!inputFieldHasFocusOld)
        {
            // Special case: the back-Action in an InputField removes its focus.
            // Thus also check that in the previous frame no InputField was focused.
            songEditorSceneControl.ReturnToLastScene();
        }
    }

    private void OnScrollWheel(InputAction.CallbackContext context)
    {
        if (GameObjectUtils.InputFieldHasFocus(eventSystem))
        {
            return;
        }

        EKeyboardModifier modifier = InputUtils.GetCurrentKeyboardModifier();

        int scrollDirection = Math.Sign(context.ReadValue<Vector2>().y);
        if (scrollDirection != 0 && noteAreaControl.IsPointerOver())
        {
            // Scroll horizontal in NoteArea with no modifier
            if (modifier == EKeyboardModifier.None)
            {
                noteAreaControl.ScrollHorizontal(scrollDirection);
            }

            // Zoom horizontal in NoteArea with Ctrl
            if (modifier == EKeyboardModifier.Ctrl)
            {
                noteAreaControl.ZoomHorizontal(scrollDirection);
            }

            // Scroll vertical in NoteArea with Shift
            if (modifier == EKeyboardModifier.Shift)
            {
                noteAreaControl.ScrollVertical(scrollDirection);
            }

            // Zoom vertical in NoteArea with Ctrl + Shift
            if (modifier == EKeyboardModifier.CtrlShift)
            {
                noteAreaControl.ZoomVertical(scrollDirection);
            }
        }
    }

    private void OnNavigate(InputAction.CallbackContext context)
    {
        if (!songAudioPlayer.IsPlaying)
        {
            Vector2 direction = context.ReadValue<Vector2>();
            EKeyboardModifier modifier = InputUtils.GetCurrentKeyboardModifier();
            
            // Scroll
            if (modifier == EKeyboardModifier.None)
            {
                if (direction.x < 0)
                {
                    noteAreaControl.ScrollHorizontal(-1);
                }
                if (direction.x > 0)
                {
                    noteAreaControl.ScrollHorizontal(1);
                }
            }
            
            List<Note> selectedNotes = selectionControl.GetSelectedNotes();
            if (selectedNotes.IsNullOrEmpty())
            {
                return;
            }
            
            // Move and stretch notes
            List<Note> followingNotes = GetFollowingNotesOrEmptyListIfDeactivated(selectedNotes);

            // Move with Shift
            if (modifier == EKeyboardModifier.Shift)
            {
                if (direction.x != 0)
                {
                    moveNotesAction.MoveNotesHorizontalAndNotify((int)direction.x, selectedNotes, followingNotes);
                }
                if (direction.y != 0)
                {
                    moveNotesAction.MoveNotesVerticalAndNotify((int)direction.y, selectedNotes, followingNotes);
                }
            }

            // Move notes one octave up / down via Ctrl+Shift
            if (modifier == EKeyboardModifier.CtrlShift)
            {
                moveNotesAction.MoveNotesVerticalAndNotify((int)direction.y * 12, selectedNotes, followingNotes);
            }

            // Extend right side with Alt
            if (modifier == EKeyboardModifier.Alt)
            {
                extendNotesAction.ExtendNotesRightAndNotify((int)direction.x, selectedNotes, followingNotes);
            }

            // Extend left side with Ctrl
            if (modifier == EKeyboardModifier.Ctrl)
            {
                extendNotesAction.ExtendNotesLeftAndNotify((int)direction.x, selectedNotes);
            }
        }
    }

    private void Update()
    {
        bool inputFieldHasFocus = GameObjectUtils.InputFieldHasFocus(eventSystem);
        if (inputFieldHasFocus)
        {
            inputFieldHasFocusOld = true;
            return;
        }

        // Move playback position in small steps by holding Ctrl when no note is selected
        if (Keyboard.current != null
            && InputUtils.IsKeyboardControlPressed()
            && (Keyboard.current.leftArrowKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
            && selectionControl.GetSelectedNotes().IsNullOrEmpty())
        {
            int stepInMillis = InputUtils.IsKeyboardShiftPressed()
                ? 1
                : 10;
            
            if (Keyboard.current.leftArrowKey.isPressed)
            {
                songAudioPlayer.PositionInSongInMillis -= stepInMillis;
            }
            else if (Keyboard.current.rightArrowKey.isPressed)
            {
                songAudioPlayer.PositionInSongInMillis += stepInMillis;
            }
        }
        
        // Use the shortcuts that are also used in the YASS song editor.
        UpdateInputForYassShortcuts();

        UpdateTouchInputForZoom();

        inputFieldHasFocusOld = inputFieldHasFocus;
    }

    private void UpdateTouchInputForZoom()
    {
        if (Touchscreen.current == null
            || GameObjectUtils.InputFieldHasFocus(eventSystem))
        {
            return;
        }

        bool isTwoFingerTouchGestureInProgress = zoomStartTouchPositions != null;
        if (Touch.activeFingers.Count < 2)
        {
            // End of gesture
            if (isTwoFingerTouchGestureInProgress)
            {
                zoomStartTouchPositions = null;
            }
        }
        else if (Touch.activeFingers.Count == 2)
        {
            Finger firstFinger = Touch.activeFingers[0];
            Finger secondFinger = Touch.activeFingers[1];
            if (isTwoFingerTouchGestureInProgress)
            {
                // Continued gesture
                UpdateTouchInputForZoomContinuedGesture(firstFinger, secondFinger);
            }
            else
            {
                // New gesture
                zoomStartTouchPositions = new Vector2[] { firstFinger.screenPosition, secondFinger.screenPosition };
                zoomStartTouchDistancePerDimension = DistancePerDimension(firstFinger.screenPosition, secondFinger.screenPosition);
            }
        }
    }

    private void UpdateTouchInputForZoomContinuedGesture(Finger firstFinger, Finger secondFinger)
    {
        // Zoom when difference to start distance is above threshold.
        Vector2 distancePerDimension = DistancePerDimension(firstFinger.screenPosition, secondFinger.screenPosition);
        Vector2 distanceDifference = distancePerDimension - zoomStartTouchDistancePerDimension;
        if (Math.Abs(distanceDifference.x) > PinchGestureMagnitudeThresholdInPixels
            || Math.Abs(distanceDifference.y) > PinchGestureMagnitudeThresholdInPixels)
        {
            if (Math.Abs(distanceDifference.x) > PinchGestureMagnitudeThresholdInPixels)
            {
                noteAreaControl.ZoomHorizontal(Math.Sign(distanceDifference.x));
            }
            if (Math.Abs(distanceDifference.y) > PinchGestureMagnitudeThresholdInPixels)
            {
                noteAreaControl.ZoomVertical(Math.Sign(distanceDifference.y));
            }
            
            zoomStartTouchPositions = new Vector2[] { firstFinger.screenPosition, secondFinger.screenPosition };
            zoomStartTouchDistancePerDimension = DistancePerDimension(firstFinger.screenPosition, secondFinger.screenPosition);
        }
    }

    private Vector2 DistancePerDimension(Vector2 a, Vector2 b)
    {
        return new Vector2(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));
    }
    
    // Implements keyboard shortcuts similar to Yass.
    // See: https://github.com/UltraStar-Deluxe/Play/issues/111
    private void UpdateInputForYassShortcuts()
    {
        EKeyboardModifier modifier = InputUtils.GetCurrentKeyboardModifier();
        if (modifier != EKeyboardModifier.None
            // Yass shortcuts only work with a keyboard.
            || Keyboard.current == null
            || GameObjectUtils.InputFieldHasFocus(eventSystem))
        {
            return;
        }
        
        // 4 and 6 on keypad to move to the previous/next note
        List<Note> selectedNotes = selectionControl.GetSelectedNotes();
        List<Note> followingNotes = GetFollowingNotesOrEmptyListIfDeactivated(selectedNotes);
        if (Keyboard.current.numpad4Key.wasReleasedThisFrame)
        {
            selectionControl.SelectPreviousNote();
        }
        if (Keyboard.current.numpad6Key.wasReleasedThisFrame)
        {
            selectionControl.SelectNextNote();
        }

        // 1 and 3 moves the note left and right (by one beat, length unchanged)
        if (Keyboard.current.numpad1Key.wasReleasedThisFrame)
        {
            moveNotesAction.MoveNotesHorizontalAndNotify(-1, selectedNotes, followingNotes);
        }
        if (Keyboard.current.numpad3Key.wasReleasedThisFrame)
        {
            moveNotesAction.MoveNotesHorizontalAndNotify(1, selectedNotes, followingNotes);
        }

        // 7 and 9 (and 8 to be more similar to division and multiply for left side) shortens/lengthens the note (by one beat, on the right side)
        if (Keyboard.current.numpad7Key.wasReleasedThisFrame
            || Keyboard.current.numpad8Key.wasReleasedThisFrame)
        {
            extendNotesAction.ExtendNotesRightAndNotify(-1, selectedNotes, followingNotes);
        }
        if (Keyboard.current.numpad9Key.wasReleasedThisFrame)
        {
            extendNotesAction.ExtendNotesRightAndNotify(1, selectedNotes, followingNotes);
        }

        // division and multiplication shortens/lengthens the note (by one beat, on the left side)
        if (Keyboard.current.numpadDivideKey.wasReleasedThisFrame)
        {
            extendNotesAction.ExtendNotesLeftAndNotify(-1, selectedNotes);
        }
        if (Keyboard.current.numpadMultiplyKey.wasReleasedThisFrame)
        {
            extendNotesAction.ExtendNotesLeftAndNotify(1, selectedNotes);
        }
        
        // Minus sign moves a note up a half-tone (due to the key's physical location, this makes sense)
        // Plus sign moves a note down a half-tone
        if (Keyboard.current.numpadMinusKey.wasReleasedThisFrame)
        {
            moveNotesAction.MoveNotesVerticalAndNotify(1, selectedNotes, followingNotes);
        }
        if (Keyboard.current.numpadPlusKey.wasReleasedThisFrame)
        {
            moveNotesAction.MoveNotesVerticalAndNotify(-1, selectedNotes, followingNotes);
        }

        // 5 plays the current selected notes (if any)
        if (Keyboard.current.numpad5Key.wasReleasedThisFrame)
        {
            if (selectedNotes.IsNullOrEmpty())
            {
                songEditorSceneControl.ToggleAudioPlayPause();
            }
            else
            {
                PlayAudioInRangeOfNotes(selectedNotes);
            }
        }

        // scroll left with h, scroll right with j
        if (Keyboard.current.hKey.wasReleasedThisFrame)
        {
            noteAreaControl.ScrollHorizontal(-1);
        }
        if (Keyboard.current.jKey.wasReleasedThisFrame)
        {
            noteAreaControl.ScrollHorizontal(1);
        }
    }

    private void PlayAudioInRangeOfNotes(List<Note> notes)
    {
        if (songAudioPlayer.IsPlaying)
        {
            return;
        }

        int minBeat = notes.Select(it => it.StartBeat).Min();
        int maxBeat = notes.Select(it => it.EndBeat).Max();
        double maxMillis = BpmUtils.BeatToMillisecondsInSong(songMeta, maxBeat);
        double minMillis = BpmUtils.BeatToMillisecondsInSong(songMeta, minBeat);
        songEditorSceneControl.StopPlaybackAfterPositionInSongInMillis = maxMillis;
        songAudioPlayer.PositionInSongInMillis = minMillis;
        songAudioPlayer.PlayAudio();
    }

    private List<Note> GetFollowingNotesOrEmptyListIfDeactivated(List<Note> selectedNotes)
    {

        if (settings.SongEditorSettings.AdjustFollowingNotes)
        {
            return SongMetaUtils.GetFollowingNotes(songMeta, selectedNotes);
        }
        else
        {
            return new List<Note>();
        }
    }
}
