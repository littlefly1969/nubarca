# Volti e persone

Come usare il riconoscimento dei volti in NubArca: trovare le persone nelle tue
foto e nei tuoi video, dare loro un nome e tenere in ordine i gruppi.

Questa pagina descrive il **prodotto**. NubArca non mostra qui i tuoi dati: i
volti, le persone e le foto restano privati e visibili solo a te.

## Dove si trova

Nel menu di navigazione, la voce **Volti** apre la pagina `/people`. È una
pagina privata: mostra solo i volti trovati nella tua libreria.

La pagina è divisa in sezioni, e la sezione selezionata vive nell'indirizzo
(`/people?tab=…`): puoi ricaricare, tornare indietro con il browser o salvare un
segnalibro su una sezione precisa.

Le sezioni sono **Gruppi suggeriti**, **Persone**, **Volti non assegnati**,
**Foto da rivedere**, **Da revisionare**, **Volti nei video**, **Ignorati** e —
per chi amministra l'installazione — **Impostazioni Face AI**.

## Il flusso normale: partire dai Gruppi suggeriti

**Gruppi suggeriti** è la sezione iniziale ed è il punto da cui conviene
partire. NubArca raggruppa da solo i volti che si somigliano e ti propone ogni
gruppo con un volto di copertina, il numero di volti che contiene e
un'indicazione di affidabilità in percentuale.

Un suggerimento è solo un suggerimento: **nulla diventa una persona finché non
lo confermi tu**. NubArca non assegna nomi da sola e non crea persone in
automatico.

Per lavorare un gruppo:

1. apri **Rivedi gruppo** per guardare i volti che contiene, uno alla volta;
2. se il gruppo è coerente, scrivi il nome nel campo **Assegna nome** e premi
   **Assegna**: il gruppo diventa una persona con quel nome;
3. se quella persona esiste già, usa invece **oppure aggiungi a…** e scegli il
   nome dall'elenco: i volti del gruppo vengono aggiunti alla persona esistente
   invece di crearne una seconda;
4. se il gruppo non ti interessa — sconosciuti, sfondo, foto di gruppo — usa
   **Ignora gruppo**. NubArca chiede conferma e sposta tutti i volti del gruppo
   in **Ignorati**, da dove potrai sempre ripristinarli.

## Persone

**Persone** elenca le persone a cui hai già dato un nome, con la loro foto di
copertina e quanti volti sono stati confermati. Finché non ne hai create,
la sezione dice «Nessuna persona ancora. Assegna un nome a un gruppo
suggerito.»

Con il campo **Cerca persona** filtri l'elenco per nome quando le persone
diventano tante.

Aprendo una persona vedi le sue foto e i suoi video, e puoi:

- **Rinomina** — cambiare il nome;
- **Rimuovi volto** — togliere un volto assegnato per errore: torna tra i volti
  non assegnati e potrà essere riassegnato;
- **Cerca volti simili** — cercare altri volti che assomigliano a questa
  persona, con una **Soglia similarità** che puoi alzare o abbassare, e
  aggiungerli con **Aggiungi**;
- **Rimuovi persona** — eliminare la persona: i volti tornano non assegnati, le
  foto non vengono toccate.

Una persona è rappresentata da un piccolo insieme di **Volti di riferimento**
scelti fra le associazioni che hai confermato. Servono alla ricerca per
similarità e puoi rigenerarli con **Ricalcola riferimenti**.

## Volti non assegnati

**Volti non assegnati** è l'elenco piatto di tutti i volti che NubArca ha
trovato e che non appartengono ancora a nessuna persona, un volto alla volta.

È la sezione giusta quando stai cercando **un** volto in particolare, invece di
lavorare per gruppi. Da qui puoi assegnare il volto a una persona esistente,
crearne una nuova, oppure ignorarlo.

## Foto da rivedere

**Foto da rivedere** guarda lo stesso lavoro dal lato opposto: invece di un
volto alla volta, ti mostra le **foto** che contengono volti ancora da decidere,
così puoi aprirne una e sistemarla fino in fondo.

Dentro una foto ti muovi con **Volto precedente** e **Volto successivo**, vedi a
che punto sei («Volto 2 di 5»), e per ogni volto puoi assegnarlo, saltarlo con
**Salta volto** o ignorarlo. **Ignora tutti i volti non assegnati** chiude in un
colpo solo i volti rimasti in quella foto.

Le due sezioni non si sostituiscono: rispondono a domande diverse.

## Da revisionare

**Da revisionare** raccoglie i gruppi che NubArca ha formato con meno
confidenza. Si lavorano esattamente come i **Gruppi suggeriti** — rivedere,
assegnare o ignorare — ma vale la pena guardarli con più attenzione prima di
dare un nome.

## Volti nei video

**Volti nei video** mostra i volti rilevati nei tuoi video. Anche qui i
suggerimenti sono solo indicazioni: un volto diventa una persona solo quando lo
confermi. Puoi confermare il suggerimento proposto, scegliere tu la persona con
**Assegna a**, oppure **Ignora**.

Una volta confermato, il video compare fra i video della persona e puoi aprirlo
al minuto in cui quella persona appare.

L'analisi dei volti nei video può essere disattivata da chi amministra
l'installazione. Quando lo è, tutto quello che è già stato riconosciuto resta
visibile e utilizzabile: si ferma solo l'analisi dei video nuovi.

## Ignorati

**Ignorati** contiene i volti che hai messo da parte. Non vengono cancellati:
con **Ripristina** tornano fra i volti non assegnati e possono essere
riassegnati o riproposti dal raggruppamento automatico.

Ignorare è quindi una decisione reversibile, ed è il modo giusto di togliere di
mezzo sconosciuti e falsi positivi senza perdere niente.

## Impostazioni Face AI

**Impostazioni Face AI** è una sezione amministrativa: compare solo a chi ha i
permessi di amministrazione dell'installazione.

Mostra se rilevamento, embedding e clustering dei volti sono attivi, qual è il
profilo in uso, e le soglie che governano quanto i gruppi sono ampi o stretti e
da che punto parte la ricerca per similarità. Da lì si avviano anche i
ricalcoli in background.

Non serve toccarla per usare i volti tutti i giorni.

## Quando il riconoscimento dei volti non è disponibile

Il riconoscimento dei volti è una funzione opzionale e può non essere attiva su
questa installazione.

Quando non lo è, NubArca lo dice invece di mostrare una pagina vuota: le sezioni
dei volti riportano «Il riconoscimento dei volti non è attivo», e la ricerca per
similarità risponde «Ricerca volti non disponibile in questo ambiente». Non è un
errore e non ci sono dati persi: chi amministra l'installazione può attivare la
funzione e far analizzare la libreria.

Se il riconoscimento è attivo ma non vedi ancora gruppi, di solito l'analisi
della libreria è semplicemente ancora in corso.

## Privacy

Tutto quello che riguarda i volti è **privato e tuo**: i volti rilevati, le
persone che hai creato, i nomi che hai scelto e i raggruppamenti restano
visibili solo a te.

I volti e le persone non compaiono mai nelle condivisioni pubbliche, e NubArca
non mette in relazione le persone di utenti diversi.
