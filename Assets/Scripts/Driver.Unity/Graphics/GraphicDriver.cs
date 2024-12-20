/*
 * (C) 2023 Radrat Softworks
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Nofun.Driver.Graphics;
using Nofun.Util;
using UnityEngine;

using System.Collections.Generic;
using System;

using UnityEngine.UI;
using UnityEngine.Rendering;
using Nofun.Module.VMGP3D;
using System.Linq;
using System.Collections;
using Nofun.Services;
using VContainer;

namespace Nofun.Driver.Unity.Graphics
{
    public class GraphicDriver : MonoBehaviour, IGraphicDriver
    {
        private readonly int NGAGE_PPI = 130;
        private const int MEASURED_CACHE_LIMIT = 4000;
        private const string BlackTransparentUniformName = "_Black_Transparent";
        private const string MainTexUniformName = "_MainTex";

        private const string Render3DZTestUniformName = "_ZTest";
        private const string Render3DCullUniformName = "_Cull";
        private const string Render3DBlendSourceFactorUniformName = "_SourceBlendFactor";
        private const string Render3DBlendDestFactorUniformName = "_DestBlendFactor";
        private const string Render3DTexturelessUniformName = "_Textureless";
        private const string Render3DTextureBlendModeUniformName = "_TextureBlendMode";
        private const string Render3DLightCountUniformName = "_LightCount";
        private const string Render3DLightPosUniformName = "_LightPos";
        private const string Render3DLightDirUniformName = "_LightDir";
        private const string Render3DLightDiffuseUniformName = "_LightDiffuse";
        private const string Render3DLightSpecularUniformName = "_LightSpecular";
        private const string Render3DLightRRangeUniformName = "_LightRRange";
        private const string Render3DLightExponentUniformName = "_LightExponent";
        private const string Render3DLightCutoffUniformName = "_LightCutoff";
        private const string Render3DLightTypeUniformName = "_LightType";
        private const string Render3DGlobalAmbientUniformName = "_GlobalAmbient";
        private const string Render3DLightMatrixUniformName = "_LightMatrix";
        private const string Render3DMaterialDiffuseUniformName = "_MaterialDiffuse";
        private const string Render3DMaterialSpecularUniformName = "_MaterialSpecular";
        private const string Render3DMaterialAmbientUniformName = "_MaterialAmbient";
        private const string Render3DMaterialEmissionUniformName = "_MaterialEmission";
        private const string Render3DMaterialShininessUniformName = "_MaterialShininess";
        private const string Render3DCameraPosUniformName = "_CameraPos";
        private const string Render3DTransparentTestUniformName = "_TransparentTest";

        private const string ZClearValueUniformName = "_ClearValue";
        private const int TMPSpawnPerAlloc = 5;

        private ScreenManager screenManager;
        private ILayoutService layoutService;

        private RenderTexture screenTextureBackBuffer;

        private bool began = false;
        private Action stopProcessor;
        private Texture2D whiteTexture;
        private Vector2 screenSize;
        private Dictionary<string, int> measuredCache;

        private TMPro.FontStyles selectedFontStyles;
        private float selectedFontSize = 11.5f;
        private float selectedOutlineWidth = 0.0f;
        private int fontMeshUsed = 0;

        private ClientState serverSideState;
        private ClientState clientSideState;
        private bool fixedStateChanged = true;

        private Matrix4x4 orthoMatrix = Matrix4x4.identity;
        private bool in3DMode = false;

        private MeshBatcher meshBatcher;

        [SerializeField]
        private TMPro.TMP_Text[] textRenders;

        [SerializeField]
        private TMPro.TMP_Text textMeasure;

        [SerializeField]
        private Material mophunDrawTextureMaterial;

        [SerializeField]
        private Material mophunMaterial;

        [SerializeField]
        private Material clearZMaterial;

        [SerializeField]
        private Camera mophunCamera;

        private CommandBuffer commandBuffer;
        private Mesh quadMesh;

        private List<TMPro.TMP_Text> textRenderInternals;
        private Dictionary<ulong, Material> materialCache;

        private List<BufferPusher> bufferPushers;
        private int bufferPusherInUse;

        private Material currentMaterial;
        private bool softwareScissor = false;
        private bool fullscreen = false;

        private enum BatchingMode
        {
            None,
            Render2D,
            Render3D,
            Render3DBillboard
        }

        private BatchingMode currentBatching = BatchingMode.None;
        private Texture2D current2DTexture = null;
        private bool currentBlackAsTransparent = false;

        [HideInInspector]
        public float FpsLimit { get; set; }

        private float SecondPerFrame => 1.0f / FpsLimit;

        private DateTime previousTime;
        private int fps = 0;
        private int currentFps = 0;

        public int Fps => fps;

        private bool rebuildLightParams = true;

        private Vector4[] lightPoses;
        private Vector4[] lightDirs;
        private Vector4[] lightDiffuses;
        private Vector4[] lightSpeculars;
        private float[] lightRRanges;
        private float[] lightExponents;
        private float[] lightCutoffAngles;
        private float[] lightTypes;
        private int lightCount;
        private bool billboarding = false;

        [Inject]
        public void Construct(ScreenManager injectScreenManager, ILayoutService injectLayoutService)
        {
            screenManager = injectScreenManager;
            layoutService = injectLayoutService;
        }

        private void Awake()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext renderContext, Camera camera)
        {
            if (camera == mophunCamera)
            {
                commandBuffer.SetRenderTarget(camera.targetTexture);

                renderContext.ExecuteCommandBuffer(commandBuffer);
                CommandBufferPool.Release(commandBuffer);

                commandBuffer = null;
            }
        }

        private Tuple<Mesh, int> GetPushedSubMesh(Func<BufferPusher, int> pushAction)
        {
            int result = pushAction(bufferPushers[bufferPusherInUse]);
            if (result < 0)
            {
                if (bufferPusherInUse + 1 >= bufferPushers.Count)
                {
                    JobScheduler.Instance.RunOnUnityThreadSync(() =>
                    {
                        bufferPushers.Add(new BufferPusher());
                    });
                }

                bufferPusherInUse++;
                int final = pushAction(bufferPushers[bufferPusherInUse]);

                if (final < 0)
                {
                    throw new OutOfMemoryException("Can't push submesh to buffer!");
                }

                return new Tuple<Mesh, int>(bufferPushers[bufferPusherInUse].BigMesh, final);
            }
            else
            {
                return new Tuple<Mesh, int>(bufferPushers[bufferPusherInUse].BigMesh, result);
            }
        }

        private Tuple<Mesh, int> GetPushedSubMesh(MpMesh rawMesh)
        {
            int result = bufferPushers[bufferPusherInUse].Push(rawMesh);
            if (result < 0)
            {
                if (bufferPusherInUse + 1 >= bufferPushers.Count)
                {
                    JobScheduler.Instance.RunOnUnityThreadSync(() =>
                    {
                        bufferPushers.Add(new BufferPusher());
                    });
                }

                bufferPusherInUse++;
                int final = bufferPushers[bufferPusherInUse].Push(rawMesh);

                if (final < 0)
                {
                    throw new OutOfMemoryException("Can't push submesh to buffer!");
                }

                return new Tuple<Mesh, int>(bufferPushers[bufferPusherInUse].BigMesh, final);
            }
            else
            {
                return new Tuple<Mesh, int>(bufferPushers[bufferPusherInUse].BigMesh, result);
            }
        }

        private void Prepare3DMaterial()
        {
            if (fixedStateChanged)
            {
                ulong stateIdentifier = serverSideState.MaterialIdentifier;
                if (!materialCache.ContainsKey(stateIdentifier))
                {
                    Material mat = new Material(mophunMaterial);
                    mat.SetFloat(Render3DCullUniformName, (float)serverSideState.cullMode.ToUnity());
                    mat.SetFloat(Render3DZTestUniformName, (float)serverSideState.depthCompareFunc.ToUnity());

                    Tuple<BlendMode, BlendMode> blendFactors = serverSideState.blendMode.ToUnity();
                    mat.SetFloat(Render3DBlendSourceFactorUniformName, (float)blendFactors.Item1);
                    mat.SetFloat(Render3DBlendDestFactorUniformName, (float)blendFactors.Item2);

                    materialCache.Add(stateIdentifier, mat);
                }
                currentMaterial = materialCache[stateIdentifier];
                fixedStateChanged = false;
            }
        }

        private void HandleFixedStateChangedClient()
        {
            FlushBatch();
        }

        public Action StopProcessorAction
        {
            set => stopProcessor = value;
        }

        private void SetupWhiteTexture()
        {
            whiteTexture = new Texture2D(1, 1);
            whiteTexture.SetPixel(0, 0, Color.white);
            whiteTexture.Apply();
        }

        private void Flush3DBatch()
        {
            if (meshBatcher.Flush())
            {
                var subMesh = GetPushedSubMesh(pusher => meshBatcher.Pop(pusher));
                bool billboard = currentBatching == BatchingMode.Render3DBillboard;

                JobScheduler.Instance.PostponeToUnityThread(() =>
                {
                    BeginRender(mode2D: false);

                    if (billboard)
                    {
                        this.billboarding = true;

                        Matrix4x4 rotScaleMat = serverSideState.viewMatrix3D;
                        for (int i = 0; i < 3; i++)
                        {
                            for (int j = 0; j < 3; j++)
                            {
                                if (i == j)
                                {
                                    rotScaleMat[i, j] = 1.0f;
                                }
                                else
                                {
                                    rotScaleMat[i, j] = 0.0f;
                                }
                            }
                        }

                        commandBuffer.SetViewMatrix(rotScaleMat);
                    }

                    commandBuffer.DrawMesh(subMesh.Item1, Matrix4x4.identity, currentMaterial, subMesh.Item2, 0, Render3DPropertyBlock);

                    if (billboard)
                    {
                        commandBuffer.SetViewMatrix(serverSideState.viewMatrix3D);
                        this.billboarding = false;
                    }
                });
            }
        }

        private void Flush2DBatch()
        {
            if (meshBatcher.Flush())
            {
                Texture2D currentTexCopy = current2DTexture;
                bool blackAsTransparentCopy = currentBlackAsTransparent;

                var subMesh = GetPushedSubMesh(pusher => meshBatcher.Pop(pusher));

                JobScheduler.Instance.PostponeToUnityThread(() =>
                {
                    BeginRender(mode2D: true);

                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    block.SetTexture(MainTexUniformName, currentTexCopy);
                    block.SetFloat(BlackTransparentUniformName, blackAsTransparentCopy ? 1.0f : 0.0f);

                    commandBuffer.DrawMesh(subMesh.Item1, Matrix4x4.identity, mophunDrawTextureMaterial, subMesh.Item2, 0, block);
                });
            }
        }

        private void FlushBatch()
        {
            switch (currentBatching)
            {
                case BatchingMode.Render2D:
                    Flush2DBatch();
                    break;

                case BatchingMode.Render3D:
                case BatchingMode.Render3DBillboard:
                    Flush3DBatch();
                    break;

                case BatchingMode.None:
                    break;

                default:
                    throw new ArgumentException("Invalid batch mode!");
            }
        }

        private void BeginBatching(BatchingMode mode = BatchingMode.Render2D)
        {
            if (currentBatching == BatchingMode.None)
            {
                currentBatching = mode;
                current2DTexture = null;

                return;
            }

            if ((mode != currentBatching) || (meshBatcher.ShouldFlush))
            {
                FlushBatch();

                currentBatching = mode;
                current2DTexture = null;
            }
        }

        private void Begin2DBatching(Texture2D currentTex, bool blackAsTransparent = false)
        {
            BeginBatching(BatchingMode.Render2D);

            if (((current2DTexture != null) && (current2DTexture != currentTex)) || (currentBlackAsTransparent != blackAsTransparent))
            {
                Flush2DBatch();
            }

            current2DTexture = currentTex;
            currentBlackAsTransparent = blackAsTransparent;
        }

        private void SetupQuadMesh()
        {
            quadMesh = new Mesh();

            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(0, 0, 0),
                new Vector3(0, 1, 0),
                new Vector3(1, 1, 0),
                new Vector3(1, 0, 0)
            };

            quadMesh.vertices = vertices;

            int[] indicies =
            {
                0, 1, 2,
                0, 2, 3
            };

            quadMesh.triangles = indicies;

            Vector2[] uv = new Vector2[4]
            {
                new Vector2(0, 1),
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1)
            };

            quadMesh.uv = uv;
        }

        private void PrepareNonFullscreenFitDisplay(RectTransform transform, AspectRatioFitter fitter, Vector2 size, Settings.ScreenOrientation newOrientation)
        {
            fitter.aspectRatio = size.x / size.y;

            if (newOrientation == Settings.ScreenOrientation.Potrait)
            {
                fitter.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
            }
            else
            {
                fitter.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(transform);

            if (newOrientation == Settings.ScreenOrientation.Landscape)
            {
                transform.localPosition = Vector3.zero;
            }
        }

        private IEnumerator PerformOrientationChange()
        {
            yield return null;

            RawImage displayImage = screenManager.CurrentDisplay;
            RectTransform imageTransform = displayImage.GetComponent<RectTransform>();

            if (displayImage.texture != screenTextureBackBuffer)
            {
                displayImage.texture = screenTextureBackBuffer;
                if (!fullscreen)
                {
                    AspectRatioFitter fitter = displayImage.gameObject.GetComponent<AspectRatioFitter>();

                    if (fitter == null)
                    {
                        fitter = displayImage.gameObject.AddComponent<AspectRatioFitter>();
                    }

                    PrepareNonFullscreenFitDisplay(imageTransform, fitter, screenSize, screenManager.ScreenOrientation);
                }
                else
                {
                    imageTransform.anchorMin = Vector2.zero;
                    imageTransform.anchorMax = Vector2.one;
                    imageTransform.offsetMax = Vector2.zero;

                    LayoutRebuilder.ForceRebuildLayoutImmediate(imageTransform);
                }
            }

            yield break;
        }

        private void OnOrientationChanged(Settings.ScreenOrientation newOrientation)
        {
            StartCoroutine(PerformOrientationChange());
        }

        public void Initialize(Vector2 size, bool softwareScissor = false)
        {
            screenManager.ScreenOrientationChanged += OnOrientationChanged;

            RawImage displayImage = screenManager.CurrentDisplay;
            Canvas canvas = layoutService.Canvas;

            RectTransform transform = displayImage.GetComponent<RectTransform>();

            transform.anchoredPosition = Vector2.zero;
            transform.offsetMin = Vector2.zero;

            fullscreen = false;

            // Assume that as cover screen
            if ((size.x <= 0) || (size.y <= 0))
            {
                // Fullscreen to get size first
                transform.anchorMin = Vector2.zero;
                transform.anchorMax = Vector2.one;
                transform.offsetMax = Vector2.zero;

                LayoutRebuilder.ForceRebuildLayoutImmediate(transform);

                size = transform.rect.size * canvas.scaleFactor;

                // For some reason, the size is not swapped on Android even when the screen has completely rotated...
                // May take few more frames, but don't risk it
                if (Application.isMobilePlatform)
                {
                    if (screenManager.ScreenOrientation != Settings.ScreenOrientation.Potrait)
                    {
                        size = new Vector2(size.y, size.x);
                    }
                }

                fullscreen = true;
            }

            screenTextureBackBuffer = new RenderTexture((int)size.x, (int)size.y, 32);
            screenTextureBackBuffer.filterMode = FilterMode.Point;

            if (!fullscreen)
            {
                AspectRatioFitter fitter = displayImage.gameObject.GetComponent<AspectRatioFitter>();

                if (fitter == null)
                {
                    fitter = displayImage.gameObject.AddComponent<AspectRatioFitter>();
                }

                PrepareNonFullscreenFitDisplay(transform, fitter, size, screenManager.ScreenOrientation);
            }

            displayImage.texture = screenTextureBackBuffer;

            meshBatcher = new();
            materialCache = new();
            measuredCache = new();
            clientSideState = new();
            serverSideState = new();
            bufferPushers = new();

            for (int i = 0; i < 5; i++)
            {
                bufferPushers.Add(new BufferPusher());
            }

            clientSideState.scissorRect = serverSideState.scissorRect = new Rect(0, 0, size.x, size.y);
            clientSideState.viewportRect = serverSideState.viewportRect = new Rect(0, 0, size.x, size.y);

            this.screenSize = size;
            this.softwareScissor = softwareScissor;

            SetupWhiteTexture();
            SetupQuadMesh();

            textRenderInternals = new(textRenders);
            orthoMatrix = Matrix4x4.Ortho(0, ScreenWidth, 0, ScreenHeight, 1, -100);
        }

        private void Start()
        {
        }

        private Rect GetUnityScreenRect(Rect curRect, bool zeroClamp = false)
        {
            float flipY = screenSize.y - (curRect.y + curRect.height);
            return new Rect(zeroClamp ? Math.Max(curRect.x, 0) : curRect.x, zeroClamp ? Math.Max(0, flipY) : flipY, curRect.width, curRect.height);
        }

        private Vector2 GetUnityCoords(float x, float y)
        {
            return new Vector2(x, screenSize.y - y);
        }

        private void DrawTexture(ITexture tex, Rect destRect, Rect sourceRect, float centerX, float centerY, float rotation, SColor color, bool blackIsTransparent, bool flipX, bool flipY)
        {
            DrawTexture(((Texture)tex).NativeTexture, destRect, sourceRect, centerX, centerY, rotation, color, blackIsTransparent, flipX, flipY);
        }

        private void DrawRectBoardGeneral(Rect destRect, Vector2[] uvs, float centerX, float centerY, float rotation, Color[] color, bool flipX = false, bool flipY = false, float z = 0.0f)
        {
            float orgX = destRect.x;
            float orgY = destRect.y;

            float sinRot = (float)Math.Sin(MathUtil.Degs2Rad(rotation));
            float cosRot = (float)Math.Cos(MathUtil.Degs2Rad(rotation));
            centerX *= -1;

            var vertices = new Vector3[]
            {
                new Vector3(orgX + centerX * cosRot - centerY * sinRot, orgY + centerX * sinRot + centerY * cosRot, z),
                new Vector3(orgX + (centerX + destRect.width) * cosRot - centerY * sinRot, orgY + (destRect.width + centerX) * sinRot + centerY * cosRot, z),
                new Vector3(orgX + centerX * cosRot - (centerY + destRect.height) * sinRot, orgY + centerX * sinRot + (centerY + destRect.height) * cosRot, z),
                new Vector3(orgX + (centerX + destRect.width) * cosRot - (centerY + destRect.height) * sinRot, orgY + (centerX + destRect.width) * sinRot + (centerY + destRect.height) * cosRot, z)
            };

            var triangles = new int[]
            {
                0, 2, 3,
                0, 3, 1
            };

            if (flipX)
            {
                (uvs[0].x, uvs[1].x) = (uvs[1].x, uvs[0].x);
                (uvs[2].x, uvs[3].x) = (uvs[3].x, uvs[2].x);
            }

            if (flipY)
            {
                (uvs[0].y, uvs[2].y) = (uvs[2].y, uvs[0].y);
                (uvs[1].y, uvs[3].y) = (uvs[3].y, uvs[1].y);
            }

            meshBatcher.AddBasic(vertices, uvs, color, triangles);
        }

        private void DrawRectBoardGeneral2D(Rect destRect, Rect sourceRect, float centerX, float centerY, float rotation, SColor color, bool flipX = false, bool flipY = false, float z = 0.0f)
        {
            if (softwareScissor)
            {
                if (!destRect.Intersects(clientSideState.scissorRect, out Rect drawArea))
                {
                    return;
                }

                if (drawArea != destRect)
                {
                    float xRatio = (drawArea.x - destRect.x) / destRect.width;
                    float yRatio = (drawArea.y - destRect.y) / destRect.height;
                    float widthRatio = drawArea.width / destRect.width;
                    float heightRatio = drawArea.height / destRect.height;

                    destRect = drawArea;

                    sourceRect.x += xRatio * sourceRect.width;
                    sourceRect.y += yRatio * sourceRect.height;
                    sourceRect.width *= widthRatio;
                    sourceRect.height *= heightRatio;
                }
            }

            destRect = GetUnityScreenRect(destRect);

            var uvs = new Vector2[]
            {
                new Vector2(sourceRect.x, sourceRect.y + sourceRect.height),
                new Vector2(sourceRect.x + sourceRect.width, sourceRect.y + sourceRect.height),
                new Vector2(sourceRect.x, sourceRect.y),
                new Vector2(sourceRect.x + sourceRect.width, sourceRect.y)
            };

            DrawRectBoardGeneral(destRect, uvs, centerX, centerY, rotation, Enumerable.Repeat(color.ToUnityColor(), 4).ToArray(), flipX, flipY, z);
        }

        private void DrawTexture(Texture2D tex, Rect destRect, Rect sourceRect, float centerX, float centerY, float rotation, SColor color, bool blackIsTransparent, bool flipX = false, bool flipY = false, float z = 0.0f)
        {
            Begin2DBatching(tex, blackIsTransparent);
            DrawRectBoardGeneral2D(destRect, sourceRect, centerX, centerY, rotation, color, flipX, flipY);
        }

        private void UpdateRenderMode()
        {
            if (in3DMode)
            {
                commandBuffer.SetProjectionMatrix(serverSideState.projectionMatrix3D);
                commandBuffer.SetViewMatrix(serverSideState.viewMatrix3D);

                if (!softwareScissor)
                {
                    commandBuffer.DisableScissorRect();
                }
            }
            else
            {
                commandBuffer.SetViewMatrix(Matrix4x4.identity);
                commandBuffer.SetProjectionMatrix(orthoMatrix);

                if (!softwareScissor)
                {
                    commandBuffer.EnableScissorRect(GetUnityScreenRect(serverSideState.scissorRect, true));
                }
            }
        }

        private void BeginRender(bool mode2D = true)
        {
            if (began)
            {
                if (mode2D != !in3DMode)
                {
                    in3DMode = !mode2D;
                    UpdateRenderMode();
                }

                Prepare3DMaterial();
                return;
            }

            if (commandBuffer == null)
            {
                commandBuffer = CommandBufferPool.Get("Mophun render buffer");
            }

            commandBuffer.Clear();

            commandBuffer.SetRenderTarget(screenTextureBackBuffer);
            commandBuffer.SetViewport(GetUnityScreenRect(serverSideState.viewportRect));

            began = true;
            in3DMode = !mode2D;

            UpdateRenderMode();
            Prepare3DMaterial();
        }

        public void EndFrame()
        {
        }

        public void ClearScreen(SColor color)
        {
            FlushBatch();

            JobScheduler.Instance.PostponeToUnityThread(() =>
            {
                BeginRender();
                commandBuffer.ClearRenderTarget(false, true, color.ToUnityColor());
            });
        }

        public void ClearDepth(float value)
        {
            FlushBatch();

            JobScheduler.Instance.PostponeToUnityThread(() =>
            {
                BeginRender();

                MaterialPropertyBlock block = new MaterialPropertyBlock();
                block.SetFloat(ZClearValueUniformName, value);

                commandBuffer.DrawMesh(quadMesh, Matrix4x4.identity, clearZMaterial, 0, 0, block);
            });
        }

        public ITexture CreateTexture(byte[] data, int width, int height, int mipCount, Driver.Graphics.TextureFormat format, Memory<SColor> palettes = new Memory<SColor>(), bool zeroAsTransparent = false)
        {
            ITexture result = null;

            JobScheduler.Instance.RunOnUnityThreadSync(() =>
            {
                result = new Texture(data, width, height, mipCount, format, palettes, zeroAsTransparent);
            });

            return result;
        }

        #region 2D draw library functions

        public void DrawText(int posX, int posY, int sizeX, int sizeY, List<int> positions, ITexture atlas, TextDirection direction, SColor textColor)
        {
            if (positions.Count % 2 != 0)
            {
                throw new ArgumentException("The list of bound values are not aligned by 2!");
            }

            // Just draw them one by one, verticies this small should not be instancing.
            // Batching them would be cool though :)
            int advX = (direction == TextDirection.Horizontal) ? sizeX : 0;
            int advY = (direction == TextDirection.VerticalUp) ? -sizeY : (direction == TextDirection.VerticalDown) ? sizeY : 0;

            Texture2D nativeTex = ((Texture)atlas).NativeTexture;

            float sizeXNormed = (float)sizeX / atlas.Width;
            float sizeYNormed = (float)sizeY / atlas.Height;

            for (int i = 0; i < positions.Count; i += 2)
            {
                Rect destRect = new Rect(posX, posY, sizeX, sizeY);
                Rect sourceRect = new Rect((float)positions[i] / atlas.Width, (float)positions[i + 1] / atlas.Height, sizeXNormed, sizeYNormed);

                DrawTexture(nativeTex, destRect, sourceRect, 0, 0, 0, textColor, false);

                posX += advX;
                posY += advY;
            }
        }

        public void DrawTexture(int posX, int posY, int centerX, int centerY, int rotation, ITexture texture,
            int sourceX = -1, int sourceY = -1, int width = -1, int height = -1, bool blackIsTransparent = false,
            bool flipX = false, bool flipY = false)
        {
            int widthToUse = (width == -1) ? texture.Width : width;
            int heightToUse = (height == -1) ? texture.Height : height;

            Rect destRect = new Rect(posX, posY, widthToUse, heightToUse);
            Rect sourceRect = new Rect(0, 0, 1, 1);

            if ((sourceX != -1) && (sourceY != -1))
            {
                sourceRect = new Rect((float)sourceX / texture.Width, (float)sourceY / texture.Height, (float)widthToUse / texture.Width, (float)heightToUse / texture.Height);
            }

            DrawTexture(texture, destRect, sourceRect, centerX, centerY, rotation, new SColor(1, 1, 1), blackIsTransparent, flipX, flipY);
        }

        private void DrawLineDetail(int x0, int y0, int x1, int y1, SColor lineColor, float lineThick)
        {
            Vector2 start = new Vector2(x0, -y0);
            Vector2 end = new Vector2(x1, -y1);

            // Our line must be reversed because the coordinate system is Y down
            Vector2 line = end - start;
            float angle = Vector2.SignedAngle(Vector2.right, line.normalized);

            Rect destRect = new Rect(x0, y0, line.magnitude, lineThick);
            DrawTexture(whiteTexture, destRect, new Rect(0, 0, 1, 1), 0, 0, angle, lineColor, false);
        }

        private void DrawLineThickScaled(int x0, int y0, int x1, int y1, SColor lineColor)
        {
            DrawLineDetail(x0, y0, x1, y1, lineColor, 1);
        }

        public void DrawLine(int x0, int y0, int x1, int y1, SColor lineColor)
        {
            DrawLineThickScaled(x0, y0, x1, y1, lineColor);
        }

        public void DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, SColor fillColor)
        {
            Begin2DBatching(whiteTexture);

            Vector3[] positions = new Vector3[]
            {
                GetUnityCoords(x0, y0),
                GetUnityCoords(x1, y1),
                GetUnityCoords(x2, y2)
            };

            int[] indices = new int[]
            {
                0, 1, 2
            };

            Vector2[] uvs = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0.5f, 1)
            };

            Color oneColor = fillColor.ToUnityColor();

            Color[] colors = new Color[]
            {
                oneColor,
                oneColor,
                oneColor
            };

            meshBatcher.AddBasic(positions, uvs, colors, indices);
        }

        public void FillRect(int x0, int y0, int x1, int y1, SColor color)
        {
            Rect destRect = new Rect(x0, y0, x1 - x0, y1 - y0);
            Rect sourceRect = new Rect(0, 0, 1, 1);

            DrawTexture(whiteTexture, destRect, sourceRect, 0, 0, 0, color, false);
        }

        #endregion

        public void FlipScreen()
        {
            DateTime currentTime = DateTime.Now;

            #region Calculate FPS
            currentFps++;

            if ((previousTime == null) || ((currentTime - previousTime).TotalSeconds > 1.0f))
            {
                fps = currentFps;
                currentFps = 0;

                previousTime = currentTime;
            }
            #endregion

            FlushBatch();

            bufferPusherInUse = 0;
            bufferPushers.ForEach(pusher =>
            {
                pusher.Flush();
            });

            JobScheduler.Instance.PostponeToUnityThread(() =>
            {
                if (began)
                {
                    mophunCamera.Render();

                    fontMeshUsed = 0;
                    meshBatcher.Reset();

                    began = false;
                }
            });

            JobScheduler.Instance.FlushPostponed();

            DateTime now = DateTime.Now;
            double remaining = SecondPerFrame - (now - currentTime).TotalSeconds;

            if (remaining > 0.0f)
            {
                // Sleep to keep up with the predefined FPS
                System.Threading.Thread.Sleep((int)(remaining * 1000));
            }
        }

        private float EmulatedPointToPixels(float point)
        {
            return point * NGAGE_PPI / 72.0f;
        }

        public void SelectSystemFont(uint fontSize, uint fontFlags, int charCodeShouldBeInFont)
        {
            // TODO: Use the character hint
            float fontSizeNew = fontSize;

            if (BitUtil.FlagSet(fontSize, SystemFontSize.PixelFlag))
            {
                fontSizeNew = (fontSize & ~(uint)SystemFontSize.PixelFlag);
            }
            else if (BitUtil.FlagSet(fontSize, SystemFontSize.PointFlag))
            {
                fontSizeNew = (fontSize & ~(uint)SystemFontSize.PointFlag);
                fontSizeNew = EmulatedPointToPixels(fontSize);
            }
            else
            {
                switch ((SystemFontSize)fontSize)
                {
                    case SystemFontSize.Large:
                        {
                            fontSizeNew = 14.5f;
                            break;
                        }

                    case SystemFontSize.Normal:
                        {
                            fontSizeNew = 13.0f;
                            break;
                        }

                    case SystemFontSize.Small:
                        {
                            fontSizeNew = 11.5f;
                            break;
                        }
                }
            }

            selectedFontSize = fontSizeNew;

            TMPro.FontStyles styles = TMPro.FontStyles.Normal;

            if (BitUtil.FlagSet(fontFlags, SystemFontStyle.Bold))
            {
                styles |= TMPro.FontStyles.Bold;
            }

            if (BitUtil.FlagSet(fontFlags, SystemFontStyle.Italic))
            {
                styles |= TMPro.FontStyles.Italic;
            }

            if (BitUtil.FlagSet(fontFlags, SystemFontStyle.Underline))
            {
                styles |= TMPro.FontStyles.Underline;
            }

            if (BitUtil.FlagSet(fontFlags, SystemFontStyle.OutlineEffect))
            {
                selectedOutlineWidth = 0.2f;
            }
            else
            {
                selectedOutlineWidth = 0.0f;
            }

            selectedFontStyles = styles;
        }

        public void DrawSystemText(short x0, short y0, string text, SColor backColor, SColor foreColor)
        {
            FlushBatch();

            JobScheduler.Instance.PostponeToUnityThread(() =>
            {
                BeginRender();

                if (textRenderInternals.Count <= fontMeshUsed)
                {
                    // Add 5 more
                    for (int i = 0; i < TMPSpawnPerAlloc; i++)
                    {
                        GameObject newObj = Instantiate(textRenders[0].gameObject, textRenders[0].transform.parent);
                        newObj.name = $"RenderText{i + fontMeshUsed}";

                        textRenderInternals.Add(newObj.GetComponent<TMPro.TMP_Text>());
                    }
                }

                TMPro.TMP_Text textRender = textRenderInternals[fontMeshUsed];

                textRender.text = text;
                textRender.color = foreColor.ToUnityColor();
                textRender.fontStyle = selectedFontStyles;
                textRender.outlineWidth = selectedOutlineWidth;
                textRender.fontSize = selectedFontSize;
                textRender.outlineColor = backColor.ToUnityColor();
                textRender.isOverlay = true;

                LayoutRebuilder.ForceRebuildLayoutImmediate(textRender.rectTransform);
                textRender.ForceMeshUpdate();

                Matrix4x4 modelMatrix = Matrix4x4.TRS(GetUnityCoords(x0, y0), Quaternion.identity, Vector3.one);

                commandBuffer.DrawMesh(textRender.mesh, modelMatrix, textRender.fontSharedMaterial);
                fontMeshUsed++;
            });
        }

        public int GetStringExtentRelativeToSystemFont(string value)
        {
            if (measuredCache.TryGetValue(value, out int cachedLength))
            {
                return cachedLength;
            }

            if (measuredCache.Count >= MEASURED_CACHE_LIMIT)
            {
                // Purge all and redo
                measuredCache.Clear();
            }

            int resultValue = 0;

            JobScheduler.Instance.RunOnUnityThreadSync(() =>
            {
                textMeasure.fontStyle = selectedFontStyles;
                textMeasure.outlineWidth = selectedOutlineWidth;
                textMeasure.fontSize = selectedFontSize;

                Vector2 size = textMeasure.GetPreferredValues(value);
                if (value == " ")
                {
                    size.x = textMeasure.fontSize / 4.0f;
                }

                resultValue = (ushort)Math.Round(size.x) | ((ushort)Math.Round(size.y) << 16);
            });

            measuredCache.Add(value, resultValue);
            return resultValue;
        }

        private static Vector2 GetPivotMultiplier(BillboardPivot pivot)
        {
            switch (pivot)
            {
                case BillboardPivot.Center:
                    return new Vector2(0.5f, 0.5f);

                case BillboardPivot.Top:
                    return new Vector2(0.5f, 1.0f);

                case BillboardPivot.TopLeft:
                    return new Vector2(0.0f, 1.0f);

                case BillboardPivot.TopRight:
                    return new Vector2(1.0f, 1.0f);

                case BillboardPivot.BottomLeft:
                    return new Vector2(0.0f, 0.0f);

                case BillboardPivot.Bottom:
                    return new Vector2(0.5f, 0.0f);

                case BillboardPivot.BottomRight:
                    return new Vector2(1.0f, 0.0f);

                case BillboardPivot.Left:
                    return new Vector2(0.0f, 0.5f);

                case BillboardPivot.Right:
                    return new Vector2(1.0f, 0.5f);

                default:
                    throw new ArgumentException($"Unhandled pivot value: {pivot}");
            }
        }

        private MaterialPropertyBlock Render3DPropertyBlock
        {
            get
            {
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                block.SetTexture(MainTexUniformName, serverSideState.textureMode ? serverSideState.mainTexture.NativeTexture : whiteTexture);
                block.SetFloat(Render3DTexturelessUniformName, serverSideState.textureMode ? 0.0f : 1.0f);
                block.SetFloat(Render3DTextureBlendModeUniformName, (float)serverSideState.textureBlendMode + 0.5f);
                if (serverSideState.lighting && !billboarding)
                {
                    if (rebuildLightParams)
                    {
                        if (lightPoses == null)
                        {
                            lightPoses = new Vector4[ClientState.MaximumLight];
                            lightDirs = new Vector4[ClientState.MaximumLight];
                            lightDiffuses = new Vector4[ClientState.MaximumLight];
                            lightSpeculars = new Vector4[ClientState.MaximumLight];
                            lightRRanges = new float[ClientState.MaximumLight];
                            lightExponents = new float[ClientState.MaximumLight];
                            lightCutoffAngles = new float[ClientState.MaximumLight];
                            lightTypes = new float[ClientState.MaximumLight];
                        }

                        lightCount = 0;
                        for (int i = 0; i < ClientState.MaximumLight; i++)
                        {
                            if (serverSideState.lights[i].lightSourceType != MpLightSourceType.Undefined)
                            {
                                lightPoses[lightCount] = serverSideState.lights[i].pos.ToUnity();
                                lightDirs[lightCount] = serverSideState.lights[i].dir.ToUnity();
                                lightDiffuses[lightCount] = serverSideState.lights[i].diffuse.ToUnityColor();
                                lightSpeculars[lightCount] = serverSideState.lights[i].specular.ToUnityColor();
                                lightRRanges[lightCount] = serverSideState.lights[i].lightRange;
                                lightExponents[lightCount] = serverSideState.lights[i].exponent;
                                lightCutoffAngles[lightCount] = serverSideState.lights[i].cutoff;
                                lightTypes[lightCount] = (float)serverSideState.lights[i].lightSourceType + 0.5f;

                                lightCount++;
                            }
                        }

                        rebuildLightParams = false;
                    }

                    block.SetInt(Render3DLightCountUniformName, lightCount);
                    block.SetVectorArray(Render3DLightPosUniformName, lightPoses);
                    block.SetVectorArray(Render3DLightDirUniformName, lightDirs);
                    block.SetVectorArray(Render3DLightDiffuseUniformName, lightDiffuses);
                    block.SetVectorArray(Render3DLightSpecularUniformName, lightSpeculars);
                    block.SetFloatArray(Render3DLightRRangeUniformName, lightRRanges);
                    block.SetFloatArray(Render3DLightExponentUniformName, lightExponents);
                    block.SetFloatArray(Render3DLightCutoffUniformName, lightCutoffAngles);
                    block.SetFloatArray(Render3DLightTypeUniformName, lightTypes);

                    block.SetColor(Render3DGlobalAmbientUniformName, serverSideState.globalAmbient.ToUnityColor());
                    block.SetMatrix(Render3DLightMatrixUniformName, serverSideState.lightMatrix3D);
                    block.SetVector(Render3DMaterialAmbientUniformName, serverSideState.extendedMaterial.ambient.ToUnityColor());
                    block.SetVector(Render3DMaterialDiffuseUniformName, serverSideState.extendedMaterial.diffuse.ToUnityColor());
                    block.SetVector(Render3DMaterialSpecularUniformName, serverSideState.extendedMaterial.specular.ToUnityColor());
                    block.SetVector(Render3DMaterialEmissionUniformName, serverSideState.extendedMaterial.emission.ToUnityColor());
                    block.SetFloat(Render3DMaterialShininessUniformName, serverSideState.extendedMaterial.shininess);

                    if (!serverSideState.specular || (serverSideState.cameraPosition == null))
                    {
                        block.SetVector(Render3DCameraPosUniformName, new Vector4(0, 0, 0, -1));
                    }
                    else
                    {
                        block.SetVector(Render3DCameraPosUniformName, new Vector4(serverSideState.cameraPosition.x, serverSideState.cameraPosition.y, serverSideState.cameraPosition.z, 1.0f));
                    }
                }
                else
                {
                    block.SetInt(Render3DLightCountUniformName, -1);
                }
                block.SetFloat(Render3DTransparentTestUniformName, serverSideState.transparentTest ? 1.0f : 0.0f);

                return block;
            }
        }

        public void DrawBillboard(NativeBillboard billboard)
        {
            BeginBatching(BatchingMode.Render3DBillboard);

            MpCullMode previousCull = Cull;
            Cull = MpCullMode.CounterClockwise;

            // We need to calculate the position in the world of the billboard
            Matrix4x4 rotScaleMat = clientSideState.viewMatrix3D;
            rotScaleMat.SetColumn(3, new Vector4(0, 0, 0, 1));

            float zFloat = FixedUtil.FixedToFloat(billboard.position.fixedZ);

            Vector3 posMoved = new Vector3(FixedUtil.FixedToFloat(billboard.position.fixedX), FixedUtil.FixedToFloat(billboard.position.fixedY), zFloat);
            Vector2 size = new Vector2(FixedUtil.FixedToFloat(billboard.fixedWidth), FixedUtil.FixedToFloat(billboard.fixedHeight));

            posMoved = rotScaleMat * posMoved;

            Vector3 center = size * GetPivotMultiplier((BillboardPivot)billboard.rotationPointFlag);
            Rect destRect = new Rect(posMoved.x, posMoved.y, size.x, size.y);

            Vector2[] uvs = new Vector2[]
            {
                billboard.uv3.ToUnity(),
                billboard.uv2.ToUnity(),
                billboard.uv0.ToUnity(),
                billboard.uv1.ToUnity(),
            };

            // NOTE: Skipping billboard colors for now since they seems to not even be affected by lighting either,
            // and sometimes the passed color seems not right
            // The original order:
            /*
                billboard.color3.ToUnity(),
                billboard.color2.ToUnity(),
                billboard.color0.ToUnity(),
                billboard.color1.ToUnity()
            */
            Color[] colors = new Color[]
            {
                Color.white,
                Color.white,
                Color.white,
                Color.white
            };

            // Need to flip the center, the 3D here is normal, no need to flip Y like 2D (DrawRectBoard is designed for drawing 2d texture)
            DrawRectBoardGeneral(destRect, uvs, center.x, -center.y, FixedUtil.Fixed11PointToFloat(billboard.rotation) * MathUtil.FullCircleDegrees, colors, z: posMoved.z);
            Cull = previousCull;
        }

        public void DrawPrimitives(MpMesh meshToDraw)
        {
            BeginBatching(BatchingMode.Render3D);

            if (meshBatcher.Batchable(meshToDraw))
            {
                meshBatcher.Add(meshToDraw);
                return;
            }
            else
            {
                FlushBatch();

                var identifier = GetPushedSubMesh(meshToDraw);

                JobScheduler.Instance.PostponeToUnityThread(() =>
                {
                    BeginRender(mode2D: false);
                    commandBuffer.DrawMesh(identifier.Item1, Matrix4x4.identity, currentMaterial, identifier.Item2, 0, Render3DPropertyBlock);
                });
            }
        }

        public void ClearLights()
        {
            bool clearedYet = true;
            for (int i = 0; i < clientSideState.lights.Length; i++)
            {
                if (clientSideState.lights[i].lightSourceType != MpLightSourceType.Undefined)
                {
                    clientSideState.lights[i].lightSourceType = MpLightSourceType.Undefined;
                    clearedYet = false;
                }
            }

            if (!clearedYet)
            {
                JobScheduler.Instance.PostponeToUnityThread(() =>
                {
                    for (int i = 0; i < serverSideState.lights.Length; i++)
                    {
                        if (serverSideState.lights[i].lightSourceType != MpLightSourceType.Undefined)
                        {
                            serverSideState.lights[i].lightSourceType = MpLightSourceType.Undefined;
                        }
                    }
                });
            }
        }

        public bool SetLight(int index, MpLight light)
        {
            if (index < 0 || index >= ClientState.MaximumLight)
            {
                return false;
            }

            if (!clientSideState.lights[index].Equals(light))
            {
                clientSideState.lights[index] = light;

                JobScheduler.Instance.PostponeToUnityThread(() =>
                {
                    serverSideState.lights[index] = light;
                    rebuildLightParams = true;
                });
            }

            return true;
        }

        public MpCullMode Cull
        {
            set
            {
                if (clientSideState.cullMode != value)
                {
                    clientSideState.cullMode = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.cullMode = value;
                        fixedStateChanged = true;
                    });
                }
            }
            get
            {
                return clientSideState.cullMode;
            }
        }

        public MpCompareFunc DepthFunction
        {
            set
            {
                if (clientSideState.depthCompareFunc != value)
                {
                    clientSideState.depthCompareFunc = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.depthCompareFunc = value;
                        fixedStateChanged = true;
                    });
                }
            }
            get => clientSideState.depthCompareFunc;
        }

        public MpBlendMode ColorBufferBlend
        {
            set
            {
                if (clientSideState.blendMode != value)
                {
                    clientSideState.blendMode = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.blendMode = value;
                        fixedStateChanged = true;
                    });
                }
            }
            get => clientSideState.blendMode;
        }

        public bool TextureMode
        {
            set
            {
                if (clientSideState.textureMode != value)
                {
                    clientSideState.textureMode = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.textureMode = value;
                    });
                }
            }
            get => clientSideState.textureMode;
        }

        public NRectangle ClipRect
        {
            set
            {
                Rect unityRect = value.ToUnity();

                if (clientSideState.scissorRect != unityRect)
                {
                    clientSideState.scissorRect = unityRect;

                    if (!softwareScissor && currentBatching == BatchingMode.Render2D)
                    {
                        HandleFixedStateChangedClient();
                    }

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.scissorRect = unityRect;

                        if (!softwareScissor && began && !in3DMode)
                        {
                            commandBuffer.EnableScissorRect(GetUnityScreenRect(unityRect, true));
                        }
                    });
                }
            }
            get => clientSideState.scissorRect.ToMophun();
        }

        public NRectangle Viewport
        {
            set
            {
                Rect unityRect = value.ToUnity();

                if (clientSideState.viewportRect != unityRect)
                {
                    clientSideState.viewportRect = unityRect;

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.viewportRect = unityRect;

                        if (began)
                        {
                            commandBuffer.SetViewport(GetUnityScreenRect(unityRect));
                        }
                    });
                }
            }
            get => clientSideState.viewportRect.ToMophun();
        }

        public Matrix4x4 ProjectionMatrix3D
        {
            get => clientSideState.projectionMatrix3D;
            set
            {
                if (clientSideState.projectionMatrix3D != value)
                {
                    clientSideState.projectionMatrix3D = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.projectionMatrix3D = value;

                        if (began && in3DMode)
                        {
                            commandBuffer.SetProjectionMatrix(value);
                        }
                    });
                }
            }
        }

        public Matrix4x4 ViewMatrix3D
        {
            get => clientSideState.viewMatrix3D;
            set
            {
                if (clientSideState.viewMatrix3D != value)
                {
                    clientSideState.viewMatrix3D = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.viewMatrix3D = value;

                        if (began && in3DMode)
                        {
                            commandBuffer.SetViewMatrix(value);
                        }
                    });
                }
            }
        }

        public Matrix4x4 LightMatrix3D
        {
            get => clientSideState.lightMatrix3D;
            set
            {
                if (clientSideState.lightMatrix3D != value)
                {
                    clientSideState.lightMatrix3D = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.lightMatrix3D = value;
                    });
                }
            }
        }

        public ITexture MainTexture
        {
            get => clientSideState.mainTexture;
            set
            {
                Texture casted = value as Texture;
                if (clientSideState.mainTexture != casted)
                {
                    clientSideState.mainTexture = casted;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.mainTexture = casted;
                    });
                }
            }
        }

        public MpTextureBlendMode TextureBlendMode
        {
            get => clientSideState.textureBlendMode;
            set
            {
                if (clientSideState.textureBlendMode != value)
                {
                    clientSideState.textureBlendMode = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.textureBlendMode = value;
                    });
                }
            }
        }

        public int ScreenWidth => (int)screenSize.x;

        public int ScreenHeight => (int)screenSize.y;

        public bool Lighting
        {
            get => clientSideState.lighting;
            set
            {
                if (clientSideState.lighting != value)
                {
                    clientSideState.lighting = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.lighting = value;
                    });
                }
            }
        }

        public bool Specular
        {
            get => clientSideState.specular;
            set
            {
                if (clientSideState.specular != value)
                {
                    clientSideState.specular = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.specular = value;
                    });
                }
            }
        }

        public bool TransparentTest
        {
            get => clientSideState.transparentTest;
            set
            {
                if (clientSideState.transparentTest != value)
                {
                    clientSideState.transparentTest = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.transparentTest = value;
                    });
                }
            }
        }

        public bool Fog
        {
            get => clientSideState.fog;
            set
            {
                if (clientSideState.fog != value)
                {
                    clientSideState.fog = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.fog = value;
                    });
                }
            }
        }

        public MpExtendedMaterial Material
        {
            get => clientSideState.extendedMaterial;
            set
            {
                if (!clientSideState.extendedMaterial.Equals(value))
                {
                    clientSideState.extendedMaterial = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.extendedMaterial = value;
                    });
                }
            }
        }

        public SColor GlobalAmbient
        {
            get => clientSideState.globalAmbient;
            set
            {
                if (!clientSideState.globalAmbient.Equals(value))
                {
                    clientSideState.globalAmbient = value;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.globalAmbient = value;
                    });
                }
            }
        }

        public NativeVector3D CameraPosition
        {
            get => clientSideState.cameraPosition.ToMophun();
            set
            {
                Vector3 uValue = value.ToUnity();
                if (clientSideState.cameraPosition != uValue)
                {
                    clientSideState.cameraPosition = uValue;
                    HandleFixedStateChangedClient();

                    JobScheduler.Instance.PostponeToUnityThread(() =>
                    {
                        serverSideState.cameraPosition = uValue;
                    });
                }
            }
        }

        public int MaxLights => ClientState.MaximumLight;
    }
}
