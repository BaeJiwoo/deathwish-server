using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace DeathWish.Client
{
    internal sealed class ClipGaitAnimator : MonoBehaviour
    {
        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private AnimationClipPlayable _walk;
        private double _walkDurationSeconds;

        internal static ClipGaitAnimator TryCreate(GameObject model)
        {
            var walk = Resources.Load<AnimationClip>("ShooterWalk");
            var stand = Resources.Load<AnimationClip>("ShooterStand");
            if (walk == null || stand == null || walk.length <= 0f)
            {
                Debug.LogWarning("Bob requires ShooterWalk and ShooterStand animation clips.");
                return null;
            }

            var player = model.AddComponent<ClipGaitAnimator>();
            player.Initialize(walk, stand);
            return player;
        }

        private void Initialize(AnimationClip walk, AnimationClip stand)
        {
            var animator = GetComponent<Animator>();
            if (animator == null)
            {
                animator = gameObject.AddComponent<Animator>();
            }
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _graph = PlayableGraph.Create("Bob locomotion");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(_graph, "Lower body", animator);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            var idle = AnimationClipPlayable.Create(_graph, stand);
            _walk = AnimationClipPlayable.Create(_graph, walk);
            _walkDurationSeconds = walk.length;
            _graph.Connect(idle, 0, _mixer, 0);
            _graph.Connect(_walk, 0, _mixer, 1);
            _mixer.SetInputWeight(0, 1f);
            output.SetSourcePlayable(_mixer);
            _graph.Play();
        }

        internal void Sample(double phase, float walkWeight)
        {
            // The delayed snapshot timeline drives both the actor's position and its clip time.
            _walk.SetTime(phase * _walkDurationSeconds);
            _mixer.SetInputWeight(0, 1f - walkWeight);
            _mixer.SetInputWeight(1, walkWeight);
            _graph.Evaluate(0f);
        }

        private void OnDestroy()
        {
            if (_graph.IsValid())
            {
                _graph.Destroy();
            }
        }
    }
}
