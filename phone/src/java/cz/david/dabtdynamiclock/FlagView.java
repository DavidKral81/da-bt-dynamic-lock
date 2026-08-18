package cz.david.dabtdynamiclock;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.view.View;

/**
 * A little flag for switching the language - drawn, not an emoji.
 *
 * Emoji flags depend on the phone font: on some devices they show up as two
 * letters (CZ / GB) or as an empty box. A drawn flag looks the same
 * everywhere and its size and the highlight of the selected language can be
 * controlled.
 */
public class FlagView extends View {

    private final boolean czech;
    private boolean active;
    private final Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path wedge = new Path();

    public FlagView(Context c, boolean czech) {
        super(c);
        this.czech = czech;
    }

    public void setActive(boolean a) {
        active = a;
        setAlpha(a ? 1f : 0.45f);      // the unselected language is only hinted
        invalidate();
    }

    @Override
    protected void onDraw(Canvas c) {
        float w = getWidth(), h = getHeight();
        if (czech) {
            p.setStyle(Paint.Style.FILL);
            p.setColor(Color.WHITE);
            c.drawRect(0, 0, w, h / 2, p);
            p.setColor(Color.parseColor("#D7141A"));
            c.drawRect(0, h / 2, w, h, p);
            p.setColor(Color.parseColor("#11457E"));
            wedge.reset();
            wedge.moveTo(0, 0);
            wedge.lineTo(w * 0.5f, h / 2);
            wedge.lineTo(0, h);
            wedge.close();
            c.drawPath(wedge, p);
        } else {
            // the British flag - simplified, but recognisable
            p.setStyle(Paint.Style.FILL);
            p.setColor(Color.parseColor("#012169"));
            c.drawRect(0, 0, w, h, p);

            c.save();
            c.clipRect(0, 0, w, h);
            p.setStyle(Paint.Style.STROKE);
            p.setStrokeWidth(h * 0.28f);            // white diagonals
            p.setColor(Color.WHITE);
            c.drawLine(0, 0, w, h, p);
            c.drawLine(w, 0, 0, h, p);
            p.setStrokeWidth(h * 0.12f);            // red diagonals
            p.setColor(Color.parseColor("#C8102E"));
            c.drawLine(0, 0, w, h, p);
            c.drawLine(w, 0, 0, h, p);
            c.restore();

            p.setStyle(Paint.Style.FILL);
            p.setColor(Color.WHITE);                // white cross
            c.drawRect(0, h * 0.33f, w, h * 0.67f, p);
            c.drawRect(w * 0.4f, 0, w * 0.6f, h, p);
            p.setColor(Color.parseColor("#C8102E"));    // red cross
            c.drawRect(0, h * 0.4f, w, h * 0.6f, p);
            c.drawRect(w * 0.45f, 0, w * 0.55f, h, p);
        }

        p.setStyle(Paint.Style.STROKE);     // a border, so it does not float
        p.setStrokeWidth(getResources().getDisplayMetrics().density);
        p.setColor(active ? Color.parseColor("#e6ebf2")
                          : Color.parseColor("#4b5563"));
        c.drawRect(0, 0, w, h, p);
    }
}
