# ClusterTruck Coop (até 7 jogadores)

Mod que deixa até 7 pessoas (host + 6) correrem as fases da campanha do ClusterTruck juntas, nos **mesmos caminhões**.

Cada jogador precisa de uma instalação própria do ClusterTruck.

## Como funciona

- **Host** escolhe a fase e simula os caminhões; os **clientes** recebem a posição de cada caminhão 30x por segundo.
  O host também repassa o estado de cada jogador para todos.
- Cada um controla o próprio corpo. Os outros aparecem como cápsulas coloridas (laranja, azul, verde, rosa) com o nome
  em cima; o cubo preto mostra para onde olham.
- **Colisão entre jogadores:** encostar em alguém dá um empurrão lateral suave, com velocidade de separação limitada.
  Não há corpo rígido: com o atraso da rede, um corpo sólido derrubava todo mundo dos caminhões. Cada fase começa
  com todos "fantasmas" (todos nascem no mesmo ponto) até vocês se afastarem.
- **Pausa universal:** se qualquer um pausar, pausa para todos, e aparece quem pausou. Qualquer um pode despausar.

### Modo corrida

- **A fase só termina quando todos chegam ao goal.** Quem chega primeiro fica esperando os outros.
- **Se alguém morrer**, quem está vivo continua; quando ninguém mais estiver correndo, a fase reinicia para todos
  (porque nem todos chegaram).
- **Largada justa:** todos ficam congelados até todo mundo carregar a fase, e os cronômetros começam juntos.
- **Ranking:** quando todos chegam, aparece o ranking da fase (mais rápido primeiro, com os pontos do jogo) e o total
  da sessão (vitórias, pontos, tempo). Cada fase também é salva em `ClusterTruckCoop_ranking.csv` na pasta do jogo.
- O host usa o botão de próxima fase da tela de vitória; os clientes vão junto. O host não consegue avançar antes de todos chegarem.
- Seu nome aparece para os outros (definido na janela COOP).
- Se alguém travar ou ficar AFK, o host pausa (ESC) e usa **Reiniciar fase (todos)** ou **Próxima fase mesmo assim**.

## Instalar (cada jogador)

1. Feche o jogo.
2. Coloque esta pasta `mod` dentro da pasta do jogo (ao lado do `Clustertruck.exe`) ou passe o caminho:
   ```
   install.bat
   ```
   ou `powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "C:\caminho\do\ClusterTruck"`
3. O instalador guarda os originais e os hashes em `Clustertruck_Data\Managed\.clustertruck-coop`. Depois de uma atualização do jogo, execute `install.bat -Uninstall` antes de reinstalar. Ficheiros alterados entretanto são preservados. Em instalações antigas, só é possível recuperar a DLL do jogo quando o backup corresponde à versão instalada.

Desinstalar: `install.bat -Uninstall` (restaura o backup e remove as DLLs do mod).

## Jogar

1. Abra o jogo e clique em **Play!** na janela de configuração (marque *Windowed* se quiser alternar janelas).
2. No menu, clique em **COOP** (ao lado de **PLAY**) ou aperte **F8**, e escreva seu nome.
3. Uma pessoa clica em **CRIAR SALA** (vira o host). A sala mostra os IPs dela, já identificados
   (Rede local, Radmin VPN, Tailscale…), com botão **Copiar**.
4. Os outros escolhem **ENTRAR NUMA SALA**, digitam esse IP e clicam em **ENTRAR**.
5. Na sala só o host escolhe a fase (Mundo/Fase) e clica em **COMEÇAR**; todos entram juntos.
   Cada jogador vê o seu personagem à frente e os colegas com os visuais da loja.
6. Na fase, **F8** mostra o placar. Com o jogo pausado (ESC, pausa para todos) aparecem as opções da sala:
   host: **Reiniciar fase (todos)**, **Próxima fase mesmo assim**, **Voltar todos para a sala**; outros: **Sair da sala**.
   Quando o host volta para a sala, todo mundo volta junto.

**Precisa estar na mesma rede?** Na mesma casa (mesmo roteador), não: use o IP "Rede local". Pela internet, a
conexão é direta por UDP: o jeito mais fácil é todos entrarem na mesma rede virtual (Radmin VPN, grátis; ou ZeroTier,
Tailscale) e usar o IP dessa rede. Sem VPN, o host precisa abrir a porta UDP 27015 no roteador.

A configuração (nome, último IP/porta) fica em `ClusterTruckCoop.cfg` na pasta do jogo.

## Gráficos e FPS

O jogo só tem um nível de qualidade ("Fantastic"), então o mod traz os seus: **F9** troca entre
**Original → Médio → Baixo → Mínimo** (também na linha "Gráficos" do painel, que mostra o FPS). Fica salvo em
`ClusterTruckCoop.cfg` (`gfx=0..3`) e vale só para aquele PC.

| Preset | O que desliga |
|---|---|
| Original | nada (igual ao jogo) |
| Médio | SSAO, anti-serrilhado temporal (e o passe de velocidade dele), sombras até 100 m com 2 cascatas |
| Baixo | + bloom/blur, vinheta e aberração cromática, sombras até 50 m com 1 cascata, 1 luz por pixel, sem filtro anisotrópico |
| Mínimo | + sem sombras |

Para PC fraco, o que mais pesa é a resolução (escolha na janela inicial do Unity). VSync fica no menu Options do jogo.

## Personagem dos outros jogadores

Modelo "Animated Human" de [Quaternius](https://opengameart.org/content/animated-human-low-poly) (CC0). Este Unity não carrega FBX com esqueleto em tempo de execução,
então `assets/export_human.py` (rodado no Blender) grava as animações como posições de vértices por quadro em `assets/human.bin`;
o instalador copia `human.bin` para `Managed`. Sem esse arquivo o mod volta para a cápsula colorida.
Cada jogador envia o próprio estado (parado, andando, correndo, no ar, morto) e os outros tocam a animação correspondente.
Teste visual: `-coop-dummy` põe um boneco na frente da câmera alternando as animações.

## Loja e aparência

Botão **LOJA** clonado do "ABILITIES" no menu principal (mesma fonte e sons). Cores grátis; roupas e acessórios custam pontos do
jogo (`pointsHandler`). A aparência são 9 bytes (`Look.cs`: 5 cores + 4 espaços de item) enviados junto do estado de cada jogador;
os itens são formas simples presas à cabeça ou ao peito, cujas posições por quadro vêm do `human.bin`. Compras e aparência ficam
no PlayerPrefs (`coopOwned`, `coopLook`), sem servidor: quem editar o registro consegue itens de graça.
Testes: `-coop-shop <aba>` abre a loja, `-coop-shopyaw <graus>` fixa o ângulo, `-coop-look <18 hex>` veste sem possuir,
`-coop-buytest` compra o item mais barato, confere e desfaz.

## Limitações conhecidas

- Até 7 jogadores (o 8º fica em "conectando..."); só fases da campanha (mapas customizados/Workshop não sincronizam).
- O cliente não escolhe fase nem reinicia; quem manda é o host.
- Todo jogador abre o jogo com pelo menos 50.000 pontos na loja de habilidades (repõe a cada vez que o jogo abre).
- Habilidades com câmera lenta do host deixam os caminhões do cliente mais lentos também.
- Paredes e objetos quebráveis quebram quando quebram no host (o host manda o aviso); os pedaços que voam depois são simulados em cada PC.
- Upload do host: ~130 KB/s por cliente (caminhões a 30 Hz, com rotação), ou seja ~800 KB/s com 7 jogadores (host + 6 clientes).

## Para desenvolvedores

- Código: `ClusterTruckCoop/` (C# net35, Harmony). Compilar: `dotnet build -c Release` (usa as DLLs do jogo em `../ClusterTruck.v1.1`, ou passe `-p:GameDir=...`).
- Por que não BepInEx: o Unity 5.3.4 deste jogo carrega os scripts num *child AppDomain* criado depois que o
  Doorstop/BepInEx inicia, então plugins do BepInEx carregam mas nunca recebem `Update`. O instalador injeta
  `ClusterTruckCoop.Loader.Init()` no início de `Manager.Awake` com Mono.Cecil.
- Ganchos de teste por linha de comando: `-coop-host`, `-coop-join <ip>`, `-coop-port <p>`, `-coop-name <nome>`, `-coop-players <n>`, `-coop-autoplay <fase>`, `-coop-autonext`, `-coop-testpause`, `-coop-roomtest`, `-coop-panel`, `-coop-gfx <0-3>`, `-coop-lag <ms>` `-coop-jitter <ms>` `-coop-loss <0-1>` (simula internet nos pacotes recebidos).
  O log (`[ClusterTruckCoop] [QA] ...`) vai para `Clustertruck_Data\output_log.txt`.
- `lib/`: HarmonyX 2.x, MonoMod e Mono.Cecil (licença MIT), copiados do BepInEx 5.4.23.2.

