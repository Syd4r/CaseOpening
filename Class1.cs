using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using Mirror;
using TMPro;
using UnityEngine;

namespace MoreGamesBase
{
    public class CaseOpening : GameBase
    {
        [Header("Scene References")]
        public Transform backgroundParent;
        public GameObject itemPrefab;
        public GameObject centerLinePrefab;
        public GameObject openCase;

        [Header("Text Reveals (Assign in Unity)")]
        public TextMeshPro revealTextBase;
        public TextMeshPro revealTextStatTrak;
        public TextMeshPro revealTextWear;
        public TextMeshPro revealTextPattern;
        public TextMeshPro revealTextTotal;

        [Header("Item Pools")]
        public GameObject[] blueItems;
        public GameObject[] purpleItems;
        public GameObject[] pinkItems;
        public GameObject[] redItems;
        public GameObject goldIcon;
        public GameObject[] goldItems;

        public enum Rarity { Blue, Purple, Pink, Red, Gold }
        public enum Wear { FactoryNew, MinimalWear, FieldTested, WellWorn, BattleScarred }

        // Settings
        private readonly float spinDuration = 6.0f;
        private readonly float itemSpacing = 0.41f;
        private readonly int itemsInStrip = 40;
        private readonly int winningSlotIndex = 35;

        // Colors
        private readonly Color colBlue = new Color(0.29f, 0.41f, 1.0f);
        private readonly Color colPurple = new Color(0.53f, 0.28f, 1.0f);
        private readonly Color colPink = new Color(0.83f, 0.17f, 0.9f);
        private readonly Color colRed = new Color(0.92f, 0.29f, 0.29f);
        private readonly Color colGold = new Color(0.89f, 0.68f, 0.22f);

        // State Tracking
        private List<GameObject> activeRewards = new List<GameObject>();
        private Transform stripContainer;
        private GameObject winningItemInstance;
        private GameObject activeCenterLine;

        // Animation Variables
        private bool isSpinning = false;
        private bool isRevealing = false;
        private bool skipReveal = false;
        private float spinTimer = 0f;
        private float startX = 0f;
        private float targetX = 0f;

        void Start()
        {
            HideRevealText();
            ApplyTextGlow(revealTextBase);
            ApplyTextGlow(revealTextStatTrak);
            ApplyTextGlow(revealTextWear);
            ApplyTextGlow(revealTextPattern);
            ApplyTextGlow(revealTextTotal);
        }

        private void ApplyTextGlow(TextMeshPro tmp)
        {
            if (tmp == null) return;
            tmp.fontMaterial = new Material(tmp.fontSharedMaterial);
            tmp.fontMaterial.EnableKeyword("OUTLINE_ON");
            tmp.fontMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.white);
            tmp.fontMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.15f);
        }

        public override bool Weaved() { return true; }

        // ==========================================
        // RPCs
        // ==========================================
        public void BroadcastSpinSequence(string text)
        {
            if (!NetworkServer.active) return;
            string methodSignature = "System.Void MoreGamesBase.CaseOpening::UserRpcSpinSequence(System.String)";
            int rpcHash = methodSignature.GetStableHashCode();
            NetworkWriterPooled writer = NetworkWriterPool.Get();
            writer.WriteString(text);
            this.SendRPCInternal(methodSignature, rpcHash, writer, 0, true);
            NetworkWriterPool.Return(writer);
        }

        public void BroadcastClearStrip(string text)
        {
            if (!NetworkServer.active) return;
            string methodSignature = "System.Void MoreGamesBase.CaseOpening::UserRpcClearStrip(System.String)";
            int rpcHash = methodSignature.GetStableHashCode();
            NetworkWriterPooled writer = NetworkWriterPool.Get();
            writer.WriteString(text);
            this.SendRPCInternal(methodSignature, rpcHash, writer, 0, true);
            NetworkWriterPool.Return(writer);
        }

        // ==========================================
        // SERVER LOGIC
        // ==========================================
        protected override void StartGame()
        {
            if (isRevealing)
            {
                skipReveal = true;
                return;
            }

            base.StartGame();

            if (base.isServer)
            {
                BroadcastClearStrip("clear");

                Rarity rolledRarity = GetRandomRarity();
                int itemIndex = GetRandomIndexForRarity(rolledRarity);
                Wear rolledWear = GetRandomWear(rolledRarity);
                bool isStatTrak = UnityEngine.Random.value <= 0.10f;
                int pattern = UnityEngine.Random.Range(1, 1001);

                float maxOffset = (itemSpacing / 2f) * 0.8f;
                float randomOffset = UnityEngine.Random.Range(-maxOffset, maxOffset);

                int randomSeed = UnityEngine.Random.Range(1, int.MaxValue);

                string networkData = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5:F3},{6}",
                    (int)rolledRarity, itemIndex, (int)rolledWear, isStatTrak ? 1 : 0, pattern, randomOffset, randomSeed);

                BroadcastSpinSequence(networkData);

                float totalWaitTime = spinDuration;
                StartCoroutine(ServerResolveSpin(rolledRarity, itemIndex, rolledWear, isStatTrak, pattern, totalWaitTime));
            }
        }

        private Rarity GetRandomRarity()
        {
            float roll = UnityEngine.Random.Range(0f, 100f);
            if (roll <= 1.0f) return Rarity.Gold;    // 1%
            if (roll <= 4.0f) return Rarity.Red;     // 3%
            if (roll <= 12.0f) return Rarity.Pink;   // 8%
            if (roll <= 35.0f) return Rarity.Purple; // 23%
            return Rarity.Blue;                      // 65%
        }

        private int GetRandomIndexForRarity(Rarity rarity)
        {
            switch (rarity)
            {
                case Rarity.Blue: return UnityEngine.Random.Range(0, blueItems.Length);
                case Rarity.Purple: return UnityEngine.Random.Range(0, purpleItems.Length);
                case Rarity.Pink: return UnityEngine.Random.Range(0, pinkItems.Length);
                case Rarity.Red: return UnityEngine.Random.Range(0, redItems.Length);
                case Rarity.Gold: return UnityEngine.Random.Range(0, goldItems.Length);
                default: return 0;
            }
        }

        private Wear GetRandomWear(Rarity rarity)
        {
            float roll = UnityEngine.Random.Range(0f, 1f);
            if (roll < 0.05f) return Wear.FactoryNew;
            if (roll < 0.15f) return Wear.MinimalWear;
            if (roll < 0.40f) return Wear.FieldTested;
            if (roll < 0.70f) return Wear.WellWorn;
            return Wear.BattleScarred;
        }

        private IEnumerator ServerResolveSpin(Rarity rarity, int itemIndex, Wear wear, bool isStatTrak, int pattern, float delay)
        {
            yield return new WaitForSeconds(delay);

            float finalMultiplier = CalculateFinalMultiplier(rarity, itemIndex, wear, isStatTrak, pattern);

            if (finalMultiplier > 0f)
            {
                this.Payout((double)finalMultiplier, ChangeType.GameResult, null, -1L);
            }

            yield return new WaitForSeconds(0.7f);
            this.ResetGame();
        }

        private float CalculateFinalMultiplier(Rarity rarity, int itemIndex, Wear wear, bool isStatTrak, int pattern)
        {
            float mult = 0f;

            if (rarity == Rarity.Blue) mult = 0.1f;
            if (rarity == Rarity.Purple) mult = 0.5f;
            if (rarity == Rarity.Pink) mult = 2.0f;
            if (rarity == Rarity.Red) mult = 8.0f;

            if (rarity == Rarity.Gold)
            {
                float[] goldMultipliers = new float[] { 23.0f, 15.0f, 15.0f, 17.0f, 35.0f, 12.0f, 12.0f, 10.0f, 4.0f, 12.0f };

                if (itemIndex >= 0 && itemIndex < goldMultipliers.Length)
                {
                    mult = goldMultipliers[itemIndex];
                }
                else
                {
                    mult = 50.0f;
                }
            }

            if (wear == Wear.FactoryNew) mult *= 2.0f;
            if (wear == Wear.MinimalWear) mult *= 1.5f;
            if (wear == Wear.FieldTested) mult *= 1.0f;
            if (wear == Wear.WellWorn) mult *= 0.8f;
            if (wear == Wear.BattleScarred) mult *= 0.5f;

            if (isStatTrak) mult *= 1.5f;

            if (rarity == Rarity.Red && itemIndex == 0 && (pattern == 661 || pattern == 151 || pattern == 955 || pattern == 321 || pattern == 387))
            {
                mult *= 50.0f; //Blue gem
            }

            return mult;
        }

        // ==========================================
        // CLIENT VISUALS & ANIMATION
        // ==========================================
        public void UserRpcClearStrip(string networkText)
        {
            if (!NetworkClient.active) return;

            if (openCase != null && openCase.activeSelf)
            {
                openCase.SetActive(false);
            }

            if (activeCenterLine != null)
            {
                Destroy(activeCenterLine);
                activeCenterLine = null;
            }

            foreach (var item in activeRewards)
            {
                if (item != null) Destroy(item);
            }
            activeRewards.Clear();
            winningItemInstance = null;

            if (stripContainer != null)
            {
                Destroy(stripContainer.gameObject);
                stripContainer = null;
            }

            HideRevealText();
            isSpinning = false;
            isRevealing = false;
            skipReveal = false;
        }

        public void UserRpcSpinSequence(string networkText)
        {
            if (!NetworkClient.active) return;

            string[] data = networkText.Split(',');
            if (data.Length != 7) return;

            Rarity winRarity = (Rarity)int.Parse(data[0]);
            int winIndex = int.Parse(data[1]);
            Wear winWear = (Wear)int.Parse(data[2]);
            bool winStatTrak = int.Parse(data[3]) == 1;
            int winPattern = int.Parse(data[4]);
            float offset = float.Parse(data[5], CultureInfo.InvariantCulture);
            int visualSeed = int.Parse(data[6]);

            GenerateVisualStrip(winRarity, winIndex, visualSeed);

            if (centerLinePrefab != null)
            {
                activeCenterLine = Instantiate(centerLinePrefab, backgroundParent, false);
                activeCenterLine.transform.localPosition = new Vector3(0f, 0f, 3f);
                activeCenterLine.transform.localScale = new Vector3(0.004f, 0.37f, 1f);
            }

            startX = 0f;
            targetX = -(winningSlotIndex * itemSpacing) + offset;

            spinTimer = 0f;
            isSpinning = true;
            isRevealing = false;
            skipReveal = false;

            StartCoroutine(WaitAndReveal(winRarity, winIndex, winWear, winStatTrak, winPattern));
        }

        private void GenerateVisualStrip(Rarity winRarity, int winIndex, int seed)
        {
            UnityEngine.Random.State oldState = UnityEngine.Random.state;

            UnityEngine.Random.InitState(seed);

            GameObject containerObj = new GameObject("StripContainer");
            stripContainer = containerObj.transform;
            stripContainer.SetParent(backgroundParent, false);

            stripContainer.localPosition = new Vector3(0f, 0f, 0.6f);
            stripContainer.localScale = Vector3.one;

            for (int i = -15; i < itemsInStrip; i++)
            {
                Rarity spawnRarity = winRarity;
                int spawnIndex = winIndex;

                if (i != winningSlotIndex)
                {
                    spawnRarity = GetRandomRarity();
                    spawnIndex = GetRandomIndexForRarity(spawnRarity);
                }

                GameObject slotAnchor = new GameObject($"Slot_{i}");
                slotAnchor.transform.SetParent(stripContainer, false);
                slotAnchor.transform.localPosition = new Vector3(i * itemSpacing, 0f, 0f);
                slotAnchor.transform.localScale = Vector3.one;

                if (i == winningSlotIndex)
                {
                    winningItemInstance = slotAnchor;
                }

                GameObject frame = Instantiate(itemPrefab);
                frame.transform.SetParent(slotAnchor.transform, false);
                frame.transform.localPosition = new Vector3(0f, -0.17f, 0.1f);
                frame.transform.localScale = new Vector3(0.38f, 0.022f, 1f);

                Transform itemBar = frame.transform.Find("itemBar");
                if (itemBar != null)
                {
                    SpriteRenderer sr = itemBar.GetComponent<SpriteRenderer>();
                    if (sr != null)
                    {
                        sr.color = GetColorForRarity(spawnRarity);
                        sr.sortingOrder = 2;
                    }
                }

                GameObject spriteToSpawn = GetPrefabRef(spawnRarity, spawnIndex, isSpinningView: true);
                if (spriteToSpawn != null)
                {
                    GameObject spriteInstance = Instantiate(spriteToSpawn);
                    spriteInstance.transform.SetParent(slotAnchor.transform, false);
                    spriteInstance.transform.localPosition = new Vector3(0f, 0f, 0.2f);
                    spriteInstance.transform.localScale = new Vector3(0.06f, 0.08f, 1f);

                    SpriteRenderer weaponSprite = spriteInstance.GetComponent<SpriteRenderer>();
                    if (weaponSprite == null) weaponSprite = spriteInstance.GetComponentInChildren<SpriteRenderer>();
                    if (weaponSprite != null) weaponSprite.sortingOrder = 5;
                }
            }

            UnityEngine.Random.state = oldState;
        }

        private IEnumerator WaitAndReveal(Rarity rarity, int itemIndex, Wear wear, bool isStatTrak, int pattern)
        {
            yield return new WaitForSeconds(spinDuration + 0.5f);

            if (activeCenterLine != null)
            {
                Destroy(activeCenterLine);
                activeCenterLine = null;
            }

            if (winningItemInstance == null) yield break;

            isRevealing = true;
            skipReveal = false;

            Vector3 finalWorldPos = winningItemInstance.transform.position;

            if (stripContainer != null)
            {
                stripContainer.gameObject.SetActive(false);
            }

            GameObject realItemPrefab = GetPrefabRef(rarity, itemIndex, isSpinningView: false);
            GameObject finalSpriteObj = Instantiate(realItemPrefab);

            finalSpriteObj.transform.SetParent(backgroundParent, true);
            finalSpriteObj.transform.position = finalWorldPos;
            finalSpriteObj.transform.localRotation = Quaternion.identity;

            activeRewards.Add(finalSpriteObj);

            SpriteRenderer finalSr = finalSpriteObj.GetComponentInChildren<SpriteRenderer>();
            if (finalSr != null) finalSr.sortingOrder = 50;

            Vector3 startLocalPos = finalSpriteObj.transform.localPosition;

            Vector3 targetLocalPos = new Vector3(0.2f, 0f, 1.5f);

            Vector3 startScale = finalSpriteObj.transform.localScale;
            Vector3 targetScale = startScale * 1.7f;
            if (rarity == Rarity.Gold)
            {
                targetScale = startScale * 2.3f;
            }


            float animTime = 0f;
            float animDuration = 0.4f;

            while (animTime < animDuration)
            {
                if (skipReveal) break;

                animTime += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, animTime / animDuration);

                finalSpriteObj.transform.localPosition = Vector3.Lerp(startLocalPos, targetLocalPos, t);
                finalSpriteObj.transform.localScale = Vector3.Lerp(startScale, targetScale, t);
                yield return null;
            }

            finalSpriteObj.transform.localPosition = targetLocalPos;
            finalSpriteObj.transform.localScale = targetScale;

            float finalMult = CalculateFinalMultiplier(rarity, itemIndex, wear, isStatTrak, pattern);

            float textDelay = 0.2f;

            if (revealTextBase != null) { revealTextBase.text = $"Rarity Multiplier: {GetBaseMultString(rarity)}"; revealTextBase.gameObject.SetActive(true); }
            if (!skipReveal) yield return StartCoroutine(WaitForSkip(textDelay));

            if (revealTextStatTrak != null)
            {
                if (isStatTrak) revealTextStatTrak.text = "<color=#eb4b4b>StatTrak™</color>";
                else revealTextStatTrak.text = "<color=#000000>No StatTrak</color>";
                revealTextStatTrak.gameObject.SetActive(true);
            }
            if (!skipReveal) yield return StartCoroutine(WaitForSkip(textDelay));

            if (revealTextWear != null) { revealTextWear.text = $"Wear: {GetWearString(wear)}"; revealTextWear.gameObject.SetActive(true); }
            if (!skipReveal) yield return StartCoroutine(WaitForSkip(textDelay));

            if (revealTextPattern != null)
            {
                if (rarity == Rarity.Red && itemIndex == 0 && pattern == 661) revealTextPattern.text = $"Pattern: {pattern} <color=#4b69ff>(BLUE GEM!)</color>";
                else revealTextPattern.text = $"Pattern: {pattern}";
                revealTextPattern.gameObject.SetActive(true);
            }
            if (!skipReveal) yield return StartCoroutine(WaitForSkip(textDelay));

            string coloredFinalMult;
            if (finalMult >= 10f) coloredFinalMult = $"<color=#d4af37>{finalMult.ToString("0.##")}x</color>"; // Deep Gold
            else if (finalMult >= 1f) coloredFinalMult = $"<color=#00ff00>{finalMult.ToString("0.##")}x</color>"; // Green
            else coloredFinalMult = $"<color=#ff0000>{finalMult.ToString("0.##")}x</color>"; // Red

            if (revealTextTotal != null) { revealTextTotal.text = $"TOTAL WIN: {coloredFinalMult}"; revealTextTotal.gameObject.SetActive(true); }

            isRevealing = false;
        }

        private IEnumerator WaitForSkip(float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                if (skipReveal) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }

        private string GetWearString(Wear w)
        {
            switch (w)
            {
                case Wear.FactoryNew: return "<color=#00ff00>FN</color>"; // Green
                case Wear.MinimalWear: return "<color=#adff2f>MW</color>"; // Yellow-Green
                case Wear.FieldTested: return "<color=#ffff00>FT</color>"; // Yellow
                case Wear.WellWorn: return "<color=#ffa500>WW</color>"; // Orange
                case Wear.BattleScarred: return "<color=#ff0000>BS</color>"; // Red
                default: return "";
            }
        }

        private GameObject GetPrefabRef(Rarity r, int index, bool isSpinningView)
        {
            if (r == Rarity.Gold)
            {
                return isSpinningView ? goldIcon : goldItems[index];
            }
            if (r == Rarity.Blue) return blueItems[index];
            if (r == Rarity.Purple) return purpleItems[index];
            if (r == Rarity.Pink) return pinkItems[index];
            if (r == Rarity.Red) return redItems[index];
            return null;
        }

        private Color GetColorForRarity(Rarity r)
        {
            switch (r)
            {
                case Rarity.Blue: return colBlue;
                case Rarity.Purple: return colPurple;
                case Rarity.Pink: return colPink;
                case Rarity.Red: return colRed;
                case Rarity.Gold: return colGold;
                default: return Color.white;
            }
        }

        void Update()
        {
            if (isRevealing)
            {
                skipReveal = true;
            }

            if (!isSpinning || stripContainer == null) return;

            spinTimer += Time.deltaTime;
            float progress = Mathf.Clamp01(spinTimer / spinDuration);

            if (progress >= 1.0f)
            {
                isSpinning = false;
            }

            float easeOutCurve = 1f - Mathf.Pow(1f - progress, 3f);
            float currentX = Mathf.Lerp(startX, targetX, easeOutCurve);

            stripContainer.localPosition = new Vector3(currentX, stripContainer.localPosition.y, stripContainer.localPosition.z);
        }

        private string GetBaseMultString(Rarity r)
        {
            if (r == Rarity.Blue) return "<color=#ff0000>0.1x</color>";
            if (r == Rarity.Purple) return "<color=#ff0000>0.5x</color>";
            if (r == Rarity.Pink) return "<color=#00ff00>2.0x</color>";
            if (r == Rarity.Red) return "<color=#d4af37>10.0x</color>";
            if (r == Rarity.Gold) return "<color=#d4af37>Variable</color>";
            return "0x";
        }

        private void HideRevealText()
        {
            if (revealTextBase) revealTextBase.gameObject.SetActive(false);
            if (revealTextStatTrak) revealTextStatTrak.gameObject.SetActive(false);
            if (revealTextWear) revealTextWear.gameObject.SetActive(false);
            if (revealTextPattern) revealTextPattern.gameObject.SetActive(false);
            if (revealTextTotal) revealTextTotal.gameObject.SetActive(false);
        }
    }
}

namespace CaseOpeningMod
{
    [BepInPlugin("com.Syd4r.caseopening", "Case Opening", "1.0.0")]
    [BepInDependency("com.moregames.base", BepInDependency.DependencyFlags.HardDependency)]
    public class CaseOpeningPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Case Opening Assembly loaded by BepInEx! Waiting for Base Loader...");
        }
    }
}