import { bindValue, trigger, useValue } from "cs2/api";

import styles from "./crosswalk-tool.module.scss";
import crosswalkIcon from "images/crosswalk.svg";

/**
 * Bindings published by CrosswalkToolUISystem. The group name is shared with the C# side and is the
 * only contract between the two halves — everything the panel shows is worked out over there,
 * because the frontend can see neither the tool system nor the selected entity.
 */
const GROUP = "crosswalkWidth";

const toolActive$ = bindValue<boolean>(GROUP, "toolActive", false);
const hasSelection$ = bindValue<boolean>(GROUP, "hasSelection", false);
const selectedPercent$ = bindValue<number>(GROUP, "selectedPercent", 100);
const globalPercent$ = bindValue<number>(GROUP, "globalPercent", 150);
const crossingNumber$ = bindValue<number>(GROUP, "crossingNumber", 0);
const crossingCount$ = bindValue<number>(GROUP, "crossingCount", 0);
const canScramble$ = bindValue<boolean>(GROUP, "canScramble", false);
const hasScramble$ = bindValue<boolean>(GROUP, "hasScramble", false);
const canRemove$ = bindValue<boolean>(GROUP, "canRemove", false);
const paintHidden$ = bindValue<boolean>(GROUP, "paintHidden", false);

/**
 * The toolbar button, and the small panel that appears beside it while the tool is running.
 *
 * The panel carries the widen and narrow buttons rather than leaving them to a keyboard shortcut,
 * because a mod tool cannot rely on the game enabling elevation input for it — see the comment at
 * the top of CrosswalkPickerToolSystem. Clicks on a junction are handled by the tool itself; these
 * buttons only act on whatever it has selected. "Spawn middle crosswalk" is the exception: it acts
 * on the junction rather than the crossing, and only shows for junctions with four or more roads
 * meeting at them, because a middle to cross is what it needs.
 */
// Every label that contains a value is built as one template literal rather than as JSX text with
// {expressions} in it. The game's interface lays its elements out with flexbox, and a label written
// the JSX way arrives as several text nodes — which become several flex items. "Same for all
// {n} crossings here" rendered as "Same for all 7crossings here", and "Back to global
// ({p}%)" broke across three lines as "Back to global (" / "150" / "%)". One node, one line.
export function CrosswalkToolButton(): JSX.Element {
  const toolActive = useValue(toolActive$);
  const hasSelection = useValue(hasSelection$);
  const selectedPercent = useValue(selectedPercent$);
  const globalPercent = useValue(globalPercent$);
  const crossingNumber = useValue(crossingNumber$);
  const crossingCount = useValue(crossingCount$);
  const canScramble = useValue(canScramble$);
  const hasScramble = useValue(hasScramble$);
  const canRemove = useValue(canRemove$);
  const paintHidden = useValue(paintHidden$);

  return (
    <>
      <button
        className={toolActive ? `${styles.button} ${styles.active}` : styles.button}
        title="Crossing width — set one junction at a time"
        onClick={() => trigger(GROUP, "toggleTool")}
      >
        <img className={styles.icon} src={crosswalkIcon} />
      </button>

      {toolActive && (
        <div className={styles.panel}>
          <div className={styles.title}>
            {hasSelection && crossingCount > 0
              ? `Crossing ${crossingNumber} of ${crossingCount}`
              : "Crossing width"}
          </div>

          {hasSelection ? (
            <>
              <div className={styles.row}>
                <button className={styles.step} onClick={() => trigger(GROUP, "narrow")}>
                  −
                </button>
                <span className={styles.value}>{`${selectedPercent}%`}</span>
                <button className={styles.step} onClick={() => trigger(GROUP, "widen")}>
                  +
                </button>
              </div>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "applyToJunction")}>
                {`Same for all ${crossingCount} crossings here`}
              </button>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "toggleHidePaint")}>
                {paintHidden ? "Show crossing paint" : "Hide crossing paint"}
              </button>

              {canScramble && (
                <>
                  <div className={styles.subtitle}>Through the middle</div>

                  <button
                    className={styles.secondary}
                    onClick={() => trigger(GROUP, "toggleScramble")}
                  >
                    {hasScramble ? "Remove middle crosswalk" : "Spawn middle crosswalk"}
                  </button>
                </>
              )}

              {canRemove && (
                <button
                  className={styles.secondary}
                  onClick={() => trigger(GROUP, "removeSelected")}
                >
                  Remove this crosswalk
                </button>
              )}

              <div className={styles.subtitle}>{`Back to global (${globalPercent}%)`}</div>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "resetSelected")}>
                Reset this crossing
              </button>

              <button className={styles.secondary} onClick={() => trigger(GROUP, "resetJunction")}>
                Reset junction crosswalks
              </button>

              <div className={styles.hint}>
                Each crossing has its own rings: the ends swing it, the middle slides it, the sides
                resize it. Resetting puts the width and the position back, and takes out any
                crossings added through the middle.
              </div>
            </>
          ) : (
            <div className={styles.hint}>
              Click a junction to select it. A ring appears on each of its crossings.
            </div>
          )}
        </div>
      )}
    </>
  );
}
