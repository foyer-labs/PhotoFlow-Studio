# PhotoFlow Studio 0.2.2

Torna lo zoom con la rotella del mouse nello Sviluppo, come in Denoiser, con un cursore per regolarlo.

## Novità

- **Zoom con la rotella** sulla foto, centrato sul punto indicato dal puntatore: da «adatta» fino all'800%
  (50, 75, 100, 150, 200, 300, 400, 600, 800%). **Ctrl +** e **Ctrl −** fanno lo stesso al centro della vista;
  **Spazio** e il doppio clic passano ancora tra «adatta» e 100%.
- **Cursore dello zoom** in basso a destra sulla foto, con i pulsanti − e + e la percentuale («Adatta · 51%»,
  «200%»); si ferma da solo sul 100%, dove si giudica il rumore.
- Ingrandita, la foto si sposta trascinandola. L'ingrandimento si vede subito; quando serve più dettaglio il
  motore calcola solo la parte visibile, con la riduzione del rumore. Tornando a «adatta» la foto intera
  ricompare senza ricalcolarla.

## Installazione

Scarica **`PhotoFlowStudio-0.2.2-Setup.exe`**. Chi ha già la 0.2.x riceve l'aggiornamento dalle Impostazioni.
Nessuna migrazione del catalogo. Il programma non è firmato: se Windows mostra «Windows ha protetto il PC»,
scegli «Ulteriori informazioni» › «Esegui comunque».

## Limiti noti

- Durante il ritaglio (C) lo zoom resta su «adatta».
- Aprendo un'altra foto lo zoom torna su «adatta».
