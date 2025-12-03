using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BNG;

[DisallowMultipleComponent]
public class WinButtonPush : MonoBehaviour {
    [Header("Button Binding")]
    [SerializeField] Button targetButton;
    [SerializeField] bool triggerOnButtonDown = true;
    [SerializeField] bool triggerOnButtonUp;

    [Header("Confetti")]
    [SerializeField] List<ParticleSystem> confettiSystems = new List<ParticleSystem>();
    [SerializeField] bool restartIfPlaying = true;

    bool _warnedMissingSystems;

    void Awake() {
        if (targetButton == null) {
            targetButton = GetComponent<Button>();
        }

        if (confettiSystems.Count == 0) {
            Debug.LogWarning("WinButtonPush has no confetti systems assigned.", this);
        }
        else {
            RemoveNullEntries();
        }

        if (targetButton == null) {
            Debug.LogWarning($"{nameof(WinButtonPush)} on {name} needs a BNG Button to listen to.", this);
            enabled = false;
        }

        foreach (var system in confettiSystems)
        {
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    void OnEnable() {
        RegisterCallbacks();
    }

    void OnDisable() {
        UnregisterCallbacks();
    }

    void RegisterCallbacks() {
        if (targetButton == null) {
            return;
        }

        targetButton.onButtonDown.AddListener(HandleButtonDown);
        targetButton.onButtonUp.AddListener(HandleButtonUp);
    }

    void UnregisterCallbacks() {
        if (targetButton == null) {
            return;
        }

        targetButton.onButtonDown.RemoveListener(HandleButtonDown);
        targetButton.onButtonUp.RemoveListener(HandleButtonUp);
    }

    void HandleButtonDown() {
        Debug.Log("Button Down");
        if (triggerOnButtonDown) {
            PlayConfetti();
        }
    }

    void HandleButtonUp() {
        if (triggerOnButtonUp) {
            PlayConfetti();
        }
    }

    void PlayConfetti() {
        bool playedAny = false;
        foreach (var system in confettiSystems) {
            if (system == null) {
                continue;
            }

            if (restartIfPlaying) {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.Clear(true);
            }

            system.Play(true);
            playedAny = true;
        }

        if (!playedAny && !_warnedMissingSystems) {
            Debug.LogWarning("WinButtonPush could not find any valid confetti ParticleSystems to play.", this);
            _warnedMissingSystems = true;
        }
    }

    void RemoveNullEntries() {
        confettiSystems = confettiSystems.Where(ps => ps != null).ToList();
    }
}
