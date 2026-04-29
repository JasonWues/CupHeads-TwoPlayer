using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    public static class RemoteProjectileVisuals
    {
        const float ShotSpeed = 720f;
        const float ShotLifetime = 1.15f;

        static Sprite _shotSprite;

        public static void SpawnBasicShot(LevelPlayerController player, WeaponEventPacket pkt)
        {
            if (player == null)
                return;

            EnsureShotSprite();
            if (_shotSprite == null)
                return;

            Vector2 direction = ResolveAim(player, pkt);
            Vector3 start = ResolveStartPosition(player, pkt, direction);

            var go = new GameObject("CupHeads_RemoteShotVisual");
            go.transform.position = start;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = _shotSprite;
            renderer.color = new Color(0.35f, 0.95f, 1f, 0.92f);
            renderer.sortingOrder = 1000;

            var visual = go.AddComponent<RemoteProjectileVisual>();
            visual.Initialize(direction * ShotSpeed, ShotLifetime);
        }

        static Vector2 ResolveAim(LevelPlayerController player, WeaponEventPacket pkt)
        {
            Vector2 direction = new Vector2(pkt.AimX, pkt.AimY);
            if (direction.sqrMagnitude < 0.01f && player.motor != null)
                direction = new Vector2(player.motor.LookDirection.x.Value, player.motor.LookDirection.y.Value);
            if (direction.sqrMagnitude < 0.01f)
                direction = Vector2.right;

            direction.Normalize();
            return direction;
        }

        static Vector3 ResolveStartPosition(LevelPlayerController player, WeaponEventPacket pkt, Vector2 direction)
        {
            Vector3 basePos = player.motor == null ? player.transform.position : player.motor.transform.position;
            if (Mathf.Abs(pkt.PosX) > 0.001f || Mathf.Abs(pkt.PosY) > 0.001f)
                basePos = new Vector3(pkt.PosX, pkt.PosY, basePos.z);

            return basePos
                + new Vector3(direction.x * 24f, direction.y * 18f + 8f, 0f);
        }

        static void EnsureShotSprite()
        {
            if (_shotSprite != null)
                return;

            var texture = new Texture2D(30, 12, TextureFormat.ARGB32, false);
            texture.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    float nx = (x - (texture.width - 1) * 0.5f) / (texture.width * 0.5f);
                    float ny = (y - (texture.height - 1) * 0.5f) / (texture.height * 0.5f);
                    float d = nx * nx + ny * ny * 2.5f;
                    float alpha = Mathf.Clamp01(1.1f - d);
                    Color color = Color.Lerp(new Color(0.15f, 0.82f, 1f, 0f), new Color(0.82f, 1f, 1f, 1f), alpha);
                    color.a = alpha * 0.95f;
                    texture.SetPixel(x, y, color);
                }
            }
            texture.Apply(false, true);
            _shotSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                1f);
        }

        sealed class RemoteProjectileVisual : MonoBehaviour
        {
            Vector3 _velocity;
            float _dieAt;

            public void Initialize(Vector2 velocity, float lifetime)
            {
                _velocity = new Vector3(velocity.x, velocity.y, 0f);
                _dieAt = Time.unscaledTime + Mathf.Max(0.05f, lifetime);
            }

            void Update()
            {
                transform.position += _velocity * Time.deltaTime;
                if (Time.unscaledTime >= _dieAt)
                    Destroy(gameObject);
            }
        }
    }
}
