using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeathWish.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace DeathWish.Client
{
    internal sealed class TeamDeathmatchHud : IDisposable
    {
        private const int NICKNAME_ROW_HEIGHT = 64;
        private const int KILL_FEED_ROWS = 5;
        private const float KILL_FEED_SECONDS = 6;
        private const float NAMEPLATE_HEIGHT_METERS = 2.1f;
        private const int SCOREBOARD_WIDTH = 818;
        private const int SCOREBOARD_HEIGHT = 308;
        private static readonly Color PanelColor = new Color32(19, 26, 38, 225);
        private static readonly Color InkColor = new Color32(23, 43, 64, 255);
        internal static readonly Color RedAccent = new Color32(245, 73, 81, 255);
        internal static readonly Color BlueAccent = new Color32(66, 153, 251, 255);
        private readonly Dictionary<long, Text> _nameplates = new Dictionary<long, Text>();
        private readonly Queue<(string Text, float ExpiresAtSeconds)> _kills = new Queue<(string, float)>();
        private readonly Font _font;
        private readonly RectTransform _nicknameRow;
        private readonly RectTransform _root;
        private readonly RectTransform _healthFill;
        private readonly Text _healthText;
        private readonly Text _killFeed;
        private readonly Text _score;
        private readonly Text _matchStatus;
        private readonly RectTransform _scoreboard;
        private readonly ScoreTable _redTable;
        private readonly ScoreTable _blueTable;

        internal InputField NicknameInput { get; }

        internal TeamDeathmatchHud(InputField roomInput, Button connectButton, Text statusText)
        {
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Arial" }, 20);
            Font font = _font != null ? _font : statusText.font;
            NicknameInput = CreateNicknameInput(roomInput, connectButton, font, out _nicknameRow);

            RectTransform canvas = (RectTransform)statusText.GetComponentInParent<Canvas>(true).transform;
            _root = CreateRect("Team deathmatch HUD", canvas);
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            RectTransform health = Place("Health bar", _root, new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(24, 24), new Vector2(246, 58));
            AddImage(health, PanelColor);
            RectTransform track = Place("Health track", health, Vector2.zero, Vector2.zero,
                new Vector2(12, 11), new Vector2(222, 9));
            AddImage(track, new Color32(60, 73, 90, 255));
            _healthFill = CreateRect("Health fill", track);
            _healthFill.anchorMin = Vector2.zero;
            _healthFill.anchorMax = Vector2.one;
            _healthFill.offsetMin = Vector2.zero;
            _healthFill.offsetMax = Vector2.zero;
            AddImage(_healthFill, new Color32(61, 212, 153, 255));
            RectTransform healthLabel = Place("Health", health, Vector2.up, Vector2.up,
                new Vector2(12, -7), new Vector2(222, 25));
            _healthText = AddText(healthLabel, font, 18, Color.white, TextAnchor.MiddleLeft);

            RectTransform scorePanel = Place("Match summary", _root, Vector2.one, Vector2.one,
                new Vector2(-24, -24), new Vector2(263, 58));
            AddImage(scorePanel, PanelColor);
            _score = AddText(Place("Score", scorePanel, Vector2.up, Vector2.up,
                new Vector2(8, -5), new Vector2(247, 26)), font, 20, Color.white, TextAnchor.MiddleCenter);
            _matchStatus = AddText(Place("Phase", scorePanel, Vector2.up, Vector2.up,
                new Vector2(8, -31), new Vector2(247, 19)), font, 15, Color.white, TextAnchor.MiddleCenter);

            RectTransform feed = Place("Kill feed", _root, Vector2.one, Vector2.one,
                new Vector2(-24, -92), new Vector2(430, 154));
            _killFeed = AddText(feed, font, 19, Color.white, TextAnchor.UpperRight);
            AddOutline(_killFeed);

            Vector2 center = Vector2.one * 0.5f;
            _scoreboard = Place("Scoreboard", _root, center, center, Vector2.zero,
                new Vector2(SCOREBOARD_WIDTH, SCOREBOARD_HEIGHT));
            AddImage(_scoreboard, new Color32(22, 31, 45, 247));
            AddText(Place("Heading", _scoreboard, Vector2.up, Vector2.up,
                new Vector2(22, -15), new Vector2(744, 34)), font, 24, Color.white, TextAnchor.MiddleLeft).text = "Scoreboard";
            _redTable = new ScoreTable(_scoreboard, "Red", RedAccent, 22, font);
            _blueTable = new ScoreTable(_scoreboard, "Blue", BlueAccent, 415, font);
            SetScoreboardVisible(false);
            _root.gameObject.SetActive(false);
        }

        private static InputField CreateNicknameInput(InputField roomInput, Button connectButton, Font font,
            out RectTransform row)
        {
            var panel = (RectTransform)roomInput.transform.parent;
            var room = (RectTransform)roomInput.transform;
            var connect = (RectTransform)connectButton.transform;
            Vector2 anchor = room.anchorMin;
            float y = connect.anchoredPosition.y;
            // The saved scene uses a top-anchored form. Keep its referenced controls in the same order.
            foreach (Transform child in panel)
            {
                if (child is RectTransform rect && rect.anchorMin == anchor && rect.anchoredPosition.y <= y)
                {
                    rect.anchoredPosition += Vector2.down * NICKNAME_ROW_HEIGHT;
                }
            }
            panel.sizeDelta += Vector2.up * NICKNAME_ROW_HEIGHT;

            float left = connect.anchoredPosition.x;
            row = Place("Nickname row", panel, anchor, Vector2.up, new Vector2(left, y + 14), new Vector2(350, 64));
            RectTransform label = Place("Nickname label", row, Vector2.up, Vector2.up,
                Vector2.zero, new Vector2(350, 22));
            AddText(label, font, 17, InkColor, TextAnchor.MiddleLeft).text = "Nickname (required for Team Deathmatch)";
            RectTransform input = Place("Nickname", row, Vector2.up, Vector2.up,
                new Vector2(0, -26), new Vector2(350, 38));
            Image background = AddImage(input, Color.white);
            background.raycastTarget = true;
            RectTransform placeholder = Place("Placeholder", input, Vector2.up, Vector2.up,
                new Vector2(12, -4), new Vector2(326, 30));
            Text placeholderText = AddText(placeholder, font, 18, new Color32(98, 114, 132, 255), TextAnchor.MiddleLeft);
            placeholderText.text = "1-16 characters";
            RectTransform value = Place("Text", input, Vector2.up, Vector2.up,
                new Vector2(12, -4), new Vector2(326, 30));
            var field = input.gameObject.AddComponent<InputField>();
            field.targetGraphic = background;
            field.textComponent = AddText(value, font, 18, InkColor, TextAnchor.MiddleLeft);
            field.placeholder = placeholderText;
            return field;
        }

        internal void SetMatch(MatchState state, MatchPlayer self)
        {
            _root.gameObject.SetActive(true);
            bool isRespawning = self.Health == 0 && state.Phase == MatchPhase.Playing;
            _healthText.text = isRespawning ? "HP 0  ·  Respawning" : $"HP {self.Health} / {MatchState.MAX_HEALTH}";
            _healthFill.anchorMax = new Vector2(self.Health / (float)MatchState.MAX_HEALTH, 1);

            var red = new List<MatchPlayer>(MatchState.MAX_TEAM_SIZE);
            var blue = new List<MatchPlayer>(MatchState.MAX_TEAM_SIZE);
            foreach (MatchPlayer player in state.Players)
            {
                (player.Team == TeamId.Red ? red : blue).Add(player);
            }
            _score.text = $"Red {state.RedScore}  ·  {state.BlueScore} Blue";
            _matchStatus.text = state.Phase switch
            {
                MatchPhase.Waiting => $"Waiting · Red {red.Count} / Blue {blue.Count}",
                MatchPhase.Finished => $"{state.Winner} wins · Hold Tab",
                _ => "Hold Tab for scoreboard"
            };
            _redTable.Refresh(red, self.PlayerId, state.RedScore);
            _blueTable.Refresh(blue, self.PlayerId, state.BlueScore);
        }

        internal void SetScoreboardVisible(bool isVisible)
        {
            if (_scoreboard != null)
            {
                _scoreboard.gameObject.SetActive(isVisible);
            }
        }

        internal void AddKill(KillEvent kill, float nowSeconds)
        {
            if (_kills.Count == KILL_FEED_ROWS)
            {
                _kills.Dequeue();
            }
            _kills.Enqueue(($"[{kill.AttackerNickname}] eliminated [{kill.VictimNickname}]", nowSeconds + KILL_FEED_SECONDS));
            UpdateKillFeed();
        }

        internal void ExpireKills(float nowSeconds)
        {
            if (_kills.Count == 0 || _kills.Peek().ExpiresAtSeconds > nowSeconds)
            {
                return;
            }
            do
            {
                _kills.Dequeue();
            }
            while (_kills.Count > 0 && _kills.Peek().ExpiresAtSeconds <= nowSeconds);
            UpdateKillFeed();
        }

        private void UpdateKillFeed()
        {
            var text = new StringBuilder();
            foreach (var kill in _kills)
            {
                text.AppendLine(kill.Text);
            }
            _killFeed.text = text.ToString().TrimEnd();
        }

        internal void ShowNameplate(MatchPlayer player, Vector3 feet, Camera camera, Color color)
        {
            Vector3 point = feet + Vector3.up * NAMEPLATE_HEIGHT_METERS;
            Vector3 viewport = camera.WorldToViewportPoint(point);
            bool isInView = viewport.z > 0 && viewport.x > 0 && viewport.x < 1 && viewport.y > 0 && viewport.y < 1;
            if (!isInView || Physics.Linecast(camera.transform.position, point, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            {
                HideNameplate(player.PlayerId);
                return;
            }

            if (!_nameplates.TryGetValue(player.PlayerId, out Text label))
            {
                RectTransform rect = Place("Nameplate " + player.PlayerId, _root, Vector2.zero,
                    Vector2.one * 0.5f, Vector2.zero, new Vector2(220, 28));
                label = AddText(rect, _healthText.font, 19, color, TextAnchor.MiddleCenter);
                AddOutline(label);
                _nameplates.Add(player.PlayerId, label);
            }
            label.text = player.Nickname;
            label.color = color;
            var screen = new Vector2(viewport.x, viewport.y);
            label.rectTransform.anchorMin = screen;
            label.rectTransform.anchorMax = screen;
            label.gameObject.SetActive(true);
        }

        internal void HideNameplate(long playerId)
        {
            if (_nameplates.TryGetValue(playerId, out Text label))
            {
                label.gameObject.SetActive(false);
            }
        }

        internal void RemoveNameplate(long playerId)
        {
            if (_nameplates.TryGetValue(playerId, out Text label))
            {
                UnityEngine.Object.Destroy(label.gameObject);
                _nameplates.Remove(playerId);
            }
        }

        internal void Reset()
        {
            SetScoreboardVisible(false);
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
            }
            _kills.Clear();
            if (_killFeed != null)
            {
                _killFeed.text = string.Empty;
            }
            foreach (Text label in _nameplates.Values)
            {
                if (label != null)
                {
                    UnityEngine.Object.Destroy(label.gameObject);
                }
            }
            _nameplates.Clear();
        }

        public void Dispose()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root.gameObject);
            }
            if (_nicknameRow != null)
            {
                UnityEngine.Object.Destroy(_nicknameRow.gameObject);
            }
            UnityEngine.Object.Destroy(_font);
        }

        private static RectTransform CreateRect(string name, RectTransform parent)
        {
            var child = new GameObject(name, typeof(RectTransform)) { layer = parent.gameObject.layer };
            var rect = (RectTransform)child.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static RectTransform Place(string name, RectTransform parent, Vector2 anchor, Vector2 pivot,
            Vector2 position, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        private static Image AddImage(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Text AddText(RectTransform rect, Font font, int size, Color color, TextAnchor alignment)
        {
            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = false;
            text.raycastTarget = false;
            return text;
        }

        private static void AddOutline(Text label)
        {
            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.95f);
        }

        private sealed class ScoreTable
        {
            private const int ROW_HEIGHT = 30;
            private static readonly string[] Headings = { "Nickname", "K", "D", "A", "Ping (ms)" };
            private static readonly int[] ColumnWidths = { 192, 36, 36, 36, 72 };
            private readonly string _team;
            private readonly Text _title;
            private readonly Text[][] _rows = new Text[MatchState.MAX_TEAM_SIZE][];

            internal ScoreTable(RectTransform scoreboard, string team, Color color, int x, Font font)
            {
                _team = team;
                RectTransform table = Place(team + " team", scoreboard, Vector2.up, Vector2.up,
                    new Vector2(x, -53), new Vector2(381, 223));
                AddImage(table, new Color32(39, 53, 73, 255));
                _title = AddText(Place("Team", table, Vector2.up, Vector2.up,
                    new Vector2(9, -3), new Vector2(206, 26)), font, 18, color, TextAnchor.MiddleLeft);
                Text[] headings = CreateRow(table, font, "Columns", -34, 14);
                for (int column = 0; column < Headings.Length; column++)
                {
                    headings[column].text = Headings[column];
                }
                for (int row = 0; row < _rows.Length; row++)
                {
                    _rows[row] = CreateRow(table, font, "Player " + (row + 1), -64 - (row * ROW_HEIGHT), 16);
                }
            }

            internal void Refresh(List<MatchPlayer> players, long localPlayerId, int score)
            {
                _title.text = $"{_team}  {score}";
                players.Sort(ComparePlayers);
                for (int index = 0; index < _rows.Length; index++)
                {
                    Text[] cells = _rows[index];
                    bool hasPlayer = index < players.Count;
                    cells[0].transform.parent.gameObject.SetActive(hasPlayer);
                    if (!hasPlayer)
                    {
                        continue;
                    }

                    MatchPlayer player = players[index];
                    cells[0].text = player.Nickname;
                    cells[0].fontStyle = player.PlayerId == localPlayerId ? FontStyle.Bold : FontStyle.Normal;
                    cells[1].text = player.Kills.ToString(CultureInfo.InvariantCulture);
                    cells[2].text = player.Deaths.ToString(CultureInfo.InvariantCulture);
                    cells[3].text = player.Assists.ToString(CultureInfo.InvariantCulture);
                    cells[4].text = player.PingMs == MatchState.UNKNOWN_PING_MS
                        ? "\u2014"
                        : player.PingMs.ToString(CultureInfo.InvariantCulture);
                }
            }

            private static int ComparePlayers(MatchPlayer left, MatchPlayer right)
            {
                int kills = right.Kills.CompareTo(left.Kills);
                if (kills != 0)
                {
                    return kills;
                }
                int deaths = left.Deaths.CompareTo(right.Deaths);
                return deaths != 0 ? deaths : left.PlayerId.CompareTo(right.PlayerId);
            }

            private static Text[] CreateRow(RectTransform table, Font font, string name, int y, int fontSize)
            {
                RectTransform row = Place(name, table, Vector2.up, Vector2.up,
                    new Vector2(9, y), new Vector2(372, ROW_HEIGHT));
                var cells = new Text[Headings.Length];
                int x = 0;
                for (int index = 0; index < cells.Length; index++)
                {
                    int width = ColumnWidths[index];
                    RectTransform cell = Place(Headings[index], row, Vector2.up, Vector2.up,
                        new Vector2(x, 0), new Vector2(width - 3, ROW_HEIGHT));
                    bool isName = index == 0;
                    cells[index] = AddText(cell, font, fontSize, Color.white,
                        isName ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
                    if (isName)
                    {
                        cells[index].resizeTextForBestFit = true;
                        cells[index].resizeTextMinSize = 10;
                        cells[index].resizeTextMaxSize = fontSize;
                    }
                    x += width;
                }
                return cells;
            }
        }
    }
}
