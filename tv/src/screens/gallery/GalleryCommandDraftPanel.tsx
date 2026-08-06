import { useState } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import { draftSummaryLines, type InterpretResponse } from '../../personal/galleryQuery';
import type { Language } from '../../i18n';

// Draft-confirmation panel for the LOCAL natural-language command. Shows the
// PROPOSED target state; the user must resolve any ambiguous person and press
// Applica/Apply before it is applied. Nothing is applied automatically. BACK =
// cancel (keeps the prior filters).
interface Props {
  result: InterpretResponse;
  lang: Language;
  // Chosen person id per ambiguous span text (already resolved to non-ambiguous).
  onApply: (choices: Record<string, string>) => void;
  onEdit: () => void;
  onCancel: () => void;
}

export function GalleryCommandDraftPanel({ result, lang, onApply, onEdit, onCancel }: Props) {
  const L = (it: string, en: string): string => (lang === 'it' ? it : en);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const names = result.resolvedPeople.map((p) => p.name ?? p.text);
  const lines = draftSummaryLines(result.draft, names, lang);
  const unresolved = result.ambiguities.some((a) => !choices[a.text]);

  return (
    <PanelShell title={L('Cercherò:', 'I will search for:')} onBack={onCancel}>
      <ScrollView contentContainerStyle={styles.body}>
        {lines.map((line) => (
          <Text key={line} style={styles.line}>• {line}</Text>
        ))}

        {result.ambiguities.map((amb) => (
          <View key={amb.text} style={styles.ambiguity}>
            <Text style={styles.question}>{L(`Quale ${amb.text}?`, `Which ${amb.text}?`)}</Text>
            <View style={styles.row}>
              {amb.candidates.map((c) => (
                <FocusableButton
                  key={c.personId}
                  label={`${choices[amb.text] === c.personId ? '✓ ' : ''}${c.name ?? amb.text}`}
                  onPress={() => setChoices((prev) => ({ ...prev, [amb.text]: c.personId }))}
                />
              ))}
            </View>
          </View>
        ))}

        <View style={styles.actions}>
          <FocusableButton
            label={L('Applica', 'Apply')}
            disabled={unresolved}
            hasTVPreferredFocus={!unresolved}
            onPress={() => onApply(choices)}
          />
          <FocusableButton label={L('Modifica', 'Edit')} onPress={onEdit} />
          <FocusableButton label={L('Annulla', 'Cancel')} onPress={onCancel} />
        </View>
      </ScrollView>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  body: { alignItems: 'center', gap: spacing.sm, paddingVertical: spacing.md },
  line: { color: colors.text, fontSize: font.body },
  ambiguity: { alignItems: 'center', gap: spacing.xs, marginTop: spacing.sm },
  question: { color: colors.text, fontSize: font.body, fontWeight: '600' },
  row: { flexDirection: 'row', gap: spacing.sm, flexWrap: 'wrap', justifyContent: 'center' },
  actions: { flexDirection: 'row', gap: spacing.md, marginTop: spacing.md },
});
